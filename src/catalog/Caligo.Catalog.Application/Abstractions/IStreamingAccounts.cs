using Caligo.Catalog.Domain.Users;

namespace Caligo.Catalog.Application.Abstractions;

public interface IStreamingAccounts
{
    Task<StreamingUser?> FindByIdentityAsync(string identityId, CancellationToken cancellationToken = default);

    Task<int?> FindUserIdAsync(string identityId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SubscriptionPlan>> GetPlansAsync(CancellationToken cancellationToken = default);

    Task<SubscriptionPlan?> FindPlanAsync(int subscriptionPlanId, CancellationToken cancellationToken = default);

    Task<bool> EmailTakenAsync(string email, CancellationToken cancellationToken = default);

    Task<StreamingUser?> CreateAsync(StreamingUser user, string identityId, CancellationToken cancellationToken = default);
}

public interface IBlogAccounts
{
    Task<string?> GetEmailAsync(string identityId, CancellationToken cancellationToken = default);
}
