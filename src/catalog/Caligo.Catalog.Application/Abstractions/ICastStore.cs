namespace Caligo.Catalog.Application.Abstractions;

public interface ICastStore
{
    Task<bool> SetPaymentAsync(int contentId, int actorId, float? payment, CancellationToken cancellationToken = default);
}
