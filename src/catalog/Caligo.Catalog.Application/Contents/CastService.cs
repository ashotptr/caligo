using Caligo.Catalog.Application.Abstractions;
using Caligo.SharedKernel;

namespace Caligo.Catalog.Application.Contents;

public class CastService
{
    public const string NegativePayment = "A payment can't be negative.";

    private readonly ICastStore _cast;

    public CastService(ICastStore cast)
    {
        _cast = cast;
    }

    public async Task<Result> SetPaymentAsync(
        int contentId, int actorId, float? payment, CancellationToken cancellationToken = default)
    {
        if (payment is { } amount && (!float.IsFinite(amount) || amount < 0))
        {
            return Result.Invalid(NegativePayment);
        }

        return await _cast.SetPaymentAsync(contentId, actorId, payment, cancellationToken)
            ? Result.Ok()
            : Result.NotFound();
    }
}
