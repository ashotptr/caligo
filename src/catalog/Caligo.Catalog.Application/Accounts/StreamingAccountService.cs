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
            return Result<StreamingProfileDto>.Conflict("You already have a profile.");
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

        var plan = command.SubscriptionPlanId is { } planId
            ? await _accounts.FindPlanAsync(planId, cancellationToken)
            : (await _accounts.GetPlansAsync(cancellationToken)).FirstOrDefault();

        if (plan is null)
        {
            return Result<StreamingProfileDto>.Invalid("Unknown plan.");
        }

        if (await _accounts.EmailTakenAsync(email, cancellationToken))
        {
            return Result<StreamingProfileDto>.Conflict(EmailTaken);
        }

        var user = StreamingUser.Create(
            command.FirstName!, command.LastName!, command.Dob, email, gender, plan.SubscriptionPlanId, today);

        var created = await _accounts.CreateAsync(user, caller.UserId!, cancellationToken);

        return created is null
            ? Result<StreamingProfileDto>.Conflict(EmailTaken)
            : Result<StreamingProfileDto>.Ok(StreamingProfileDto.From(created, today));
    }

    public const string ProfileRequired = "Set up your profile first.";

    private const string EmailTaken = "Your e-mail address already has a profile.";

    private static PlanDto ToDto(SubscriptionPlan plan) => new(
        plan.SubscriptionPlanId, plan.PaymentInfo, plan.PlanInfo, PlanPricing.MonthlyPrice(plan.PaymentInfo));

    private DateOnly Today() => DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
}
