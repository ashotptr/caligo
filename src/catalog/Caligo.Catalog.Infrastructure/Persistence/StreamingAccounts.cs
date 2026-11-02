using Microsoft.EntityFrameworkCore;
using Npgsql;
using Caligo.Catalog.Application.Abstractions;
using Caligo.Catalog.Domain.Users;

namespace Caligo.Catalog.Infrastructure.Persistence
{
    public class StreamingAccounts : IStreamingAccounts
    {
        private readonly StreamingDbContext _db;

        public StreamingAccounts(StreamingDbContext db)
        {
            _db = db;
        }

        public Task<StreamingUser?> FindByIdentityAsync(string identityId, CancellationToken cancellationToken = default) =>
            (from link in _db.BlogUsers
             join user in _db.Users on link.UserId equals user.UserId
             where link.IdentityId == identityId
             select user).FirstOrDefaultAsync(cancellationToken);

        public async Task<int?> FindUserIdAsync(string identityId, CancellationToken cancellationToken = default)
        {
            var ids = await _db.BlogUsers.AsNoTracking()
                .Where(link => link.IdentityId == identityId)
                .Select(link => link.UserId)
                .Take(1)
                .ToListAsync(cancellationToken);

            return ids.Count == 0 ? null : ids[0];
        }

        public async Task<IReadOnlyList<SubscriptionPlan>> GetPlansAsync(CancellationToken cancellationToken = default) =>
            await _db.SubscriptionPlans.AsNoTracking()
                .OrderBy(p => p.SubscriptionPlanId)
                .ToListAsync(cancellationToken);

        public Task<SubscriptionPlan?> FindPlanAsync(int subscriptionPlanId, CancellationToken cancellationToken = default) =>
            _db.SubscriptionPlans.AsNoTracking()
                .FirstOrDefaultAsync(p => p.SubscriptionPlanId == subscriptionPlanId, cancellationToken);

        public Task<bool> EmailTakenAsync(string email, CancellationToken cancellationToken = default) =>
            _db.Users.AnyAsync(u => u.Email == email, cancellationToken);

        public async Task<StreamingUser?> CreateAsync(
            StreamingUser user, string identityId, CancellationToken cancellationToken = default)
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

            try
            {
                _db.Users.Add(user);

                await _db.SaveChangesAsync(cancellationToken);

                _db.BlogUsers.Add(new BlogUser { UserId = user.UserId, IdentityId = identityId });

                await _db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                return user;
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                await transaction.RollbackAsync(cancellationToken);

                _db.ChangeTracker.Clear();

                return null;
            }
        }
    }
}
