using Caligo.Catalog.Application.Abstractions;
using Caligo.Catalog.Domain.Users;
using Caligo.SharedKernel;

namespace Caligo.Catalog.Application.Accounts;

public class StreamingAccountService
{
    private readonly IStreamingAccounts _accounts;
    private readonly IBlogAccounts _blog;
    private readonly TimeProvider _time;

    public StreamingAccountService(IStreamingAccounts accounts, IBlogAccounts blog, TimeProvider time)
    {
        _accounts = accounts;
        _blog = blog;
        _time = time;
    }

    public async Task<IReadOnlyList<PlanDto>> GetPlansAsync(CancellationToken cancellationToken = default)
    {
        var plans = await _accounts.GetPlansAsync(cancellationToken);

        return plans.Select(ToDto).ToList();
    }

    public async Task<Result<StreamingProfileDto>> GetProfileAsync(
        Caller caller, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAuthenticated)
        {
            return Result<StreamingProfileDto>.Unauthorized();
        }

        var user = await _accounts.FindByIdentityAsync(caller.UserId!, cancellationToken);

        return user is null
            ? Result<StreamingProfileDto>.NotFound()
            : Result<StreamingProfileDto>.Ok(StreamingProfileDto.From(user, Today()));
    }

    public async Task<Result<StreamingProfileDto>> CreateProfileAsync(
        Caller caller, CreateProfileCommand command, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAuthenticated)
        {
            return Result<StreamingProfileDto>.Unauthorized();
        }

        if (await _accounts.FindUserIdAsync(caller.UserId!, cancellationToken) is not null)
        {
            return Result<StreamingProfileDto>.Conflict("You already have a streaming profile.");
        }

        if (!Enum.TryParse<Gender>(command.Gender, ignoreCase: true, out var gender) || !Enum.IsDefined(gender))
        {
            return Result<StreamingProfileDto>.Invalid("Gender must be Male, Female or Other.");
        }

        var email = await _blog.GetEmailAsync(caller.UserId!, cancellationToken);

        if (string.IsNullOrWhiteSpace(email))
        {
            return Result<StreamingProfileDto>.Invalid("Your account has no e-mail address.");
        }

        var today = Today();
        var problem = StreamingUser.Validate(command.FirstName, command.LastName, command.Dob, email, today);

        if (problem is not null)
        {
            return Result<StreamingProfileDto>.Invalid(problem);
        }

        var plan = await _accounts.FindPlanAsync(command.SubscriptionPlanId, cancellationToken);

        if (plan is null)
        {
            return Result<StreamingProfileDto>.Invalid("Unknown subscription plan.");
        }

        if (await _accounts.EmailTakenAsync(email, cancellationToken))
        {
            return Result<StreamingProfileDto>.Conflict("That e-mail already belongs to a streaming user.");
        }

        var user = StreamingUser.Create(
            command.FirstName!, command.LastName!, command.Dob, email, gender, plan.SubscriptionPlanId, today);

        var created = await _accounts.CreateAsync(
            user, caller.UserId!, PlanPricing.MonthlyPrice(plan.PaymentInfo), Now(), cancellationToken);

        return created is null
            ? Result<StreamingProfileDto>.Conflict("That e-mail already belongs to a streaming user.")
            : Result<StreamingProfileDto>.Ok(StreamingProfileDto.From(created, today));
    }

    public async Task<Result<StreamingProfileDto>> RenewAsync(
        Caller caller, int subscriptionPlanId, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAuthenticated)
        {
            return Result<StreamingProfileDto>.Unauthorized();
        }

        var user = await _accounts.FindByIdentityAsync(caller.UserId!, cancellationToken);

        if (user is null)
        {
            return Result<StreamingProfileDto>.Conflict(ProfileRequired);
        }

        var plan = await _accounts.FindPlanAsync(subscriptionPlanId, cancellationToken);

        if (plan is null)
        {
            return Result<StreamingProfileDto>.Invalid("Unknown subscription plan.");
        }

        var today = Today();

        user.Renew(plan.SubscriptionPlanId, today);

        await _accounts.RenewAsync(user, PlanPricing.MonthlyPrice(plan.PaymentInfo), Now(), cancellationToken);

        return Result<StreamingProfileDto>.Ok(StreamingProfileDto.From(user, today));
    }

    public async Task<Result<SpendingDto>> GetSpendingAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAuthenticated)
        {
            return Result<SpendingDto>.Unauthorized();
        }

        var userId = await _accounts.FindUserIdAsync(caller.UserId!, cancellationToken);

        if (userId is null)
        {
            return Result<SpendingDto>.Conflict(ProfileRequired);
        }

        var total = await _accounts.GetAllTimeSpendingAsync(userId.Value, cancellationToken);
        var payments = await _accounts.GetPaymentsAsync(userId.Value, cancellationToken);

        return Result<SpendingDto>.Ok(new SpendingDto(
            total,
            payments.Select(p => new PaymentDto(p.PaymentId, p.Date, p.Amount, p.SubscriptionPlanId)).ToList()));
    }

    public const string ProfileRequired = "Create your streaming profile first.";

    private static PlanDto ToDto(SubscriptionPlan plan) => new(
        plan.SubscriptionPlanId, plan.PaymentInfo, plan.PlanInfo, PlanPricing.MonthlyPrice(plan.PaymentInfo));

    private DateOnly Today() => DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);

    private DateTime Now() => DateTime.SpecifyKind(_time.GetUtcNow().UtcDateTime, DateTimeKind.Unspecified);
}
