using Microsoft.EntityFrameworkCore;
using Caligo.Catalog.Application.Abstractions;

namespace Caligo.Catalog.Infrastructure.Persistence
{
    public class CastStore : ICastStore
    {
        private readonly StreamingDbContext _db;

        public CastStore(StreamingDbContext db)
        {
            _db = db;
        }

        public async Task<bool> SetPaymentAsync(
            int contentId, int actorId, float? payment, CancellationToken cancellationToken = default) =>
            await _db.ContentActors
                .Where(link => link.ContentId == contentId && link.ActorId == actorId)
                .ExecuteUpdateAsync(set => set.SetProperty(link => link.Payment, payment), cancellationToken) > 0;
    }
}
