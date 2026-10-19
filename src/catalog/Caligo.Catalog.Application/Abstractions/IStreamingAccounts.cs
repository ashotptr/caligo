using Caligo.Catalog.Domain.Users;

namespace Caligo.Catalog.Application.Abstractions;

public interface IStreamingAccounts
{
    Task<StreamingUser?> FindByIdentityAsync(string identityId, CancellationToken cancellationToken = default);

    Task<int?> FindUserIdAsync(string identityId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SubscriptionPlan>> GetPlansAsync(CancellationToken cancellationToken = default);

    Task<SubscriptionPlan?> FindPlanAsync(int subscriptionPlanId, CancellationToken cancellationToken = default);

    Task<bool> EmailTakenAsync(string email, CancellationToken cancellationToken = default);

    Task<StreamingUser?> CreateAsync(
        StreamingUser user, string identityId, float amount, DateTime paidAt,
        CancellationToken cancellationToken = default);

    Task RenewAsync(StreamingUser user, float amount, DateTime paidAt, CancellationToken cancellationToken = default);

    Task<float> GetAllTimeSpendingAsync(int userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Payment>> GetPaymentsAsync(int userId, CancellationToken cancellationToken = default);
}

public interface IBlogAccounts
{
    Task<string?> GetEmailAsync(string identityId, CancellationToken cancellationToken = default);
}
