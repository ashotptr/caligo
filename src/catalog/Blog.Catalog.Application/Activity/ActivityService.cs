using Blog.Catalog.Application.Abstractions;
using Blog.Catalog.Application.Accounts;
using Blog.Catalog.Application.Contents;
using Blog.Catalog.Domain.Activity;
using Blog.SharedKernel;

namespace Blog.Catalog.Application.Activity;

public class ActivityService
{
    public const int HistoryLimit = 100;
    public const float MaxTimestamp = 100_000f;

    private readonly ICatalogReader _catalog;
    private readonly IStreamingAccounts _accounts;
    private readonly IActivityStore _activity;
    private readonly TimeProvider _time;

    public ActivityService(
        ICatalogReader catalog, IStreamingAccounts accounts, IActivityStore activity, TimeProvider time)
    {
        _catalog = catalog;
        _accounts = accounts;
        _activity = activity;
        _time = time;
    }

    public async Task<Result<InteractionDto>> GetInteractionAsync(
        int contentId, Caller caller, CancellationToken cancellationToken = default)
    {
        var who = await ResolveAsync<InteractionDto>(caller, contentId, cancellationToken);

        if (who.Failure is { } failure)
        {
            return failure;
        }

        var interaction = await _activity.GetInteractionAsync(contentId, who.UserId, cancellationToken);

        return interaction is null
            ? Result<InteractionDto>.NotFound()
            : Result<InteractionDto>.Ok(interaction);
    }

    public async Task<Result<InteractionDto>> SetInteractionAsync(
        int contentId, SetInteractionCommand command, Caller caller, CancellationToken cancellationToken = default)
    {
        if (command.Liked is null && command.Rating is null)
        {
            return Result<InteractionDto>.Invalid("Send a rating, a like, or both.");
        }

        if (!UserContentInteraction.IsValidRating(command.Rating))
        {
            return Result<InteractionDto>.Invalid(
                $"Rating must be between {UserContentInteraction.MinRating} and {UserContentInteraction.MaxRating}.");
        }

        var who = await ResolveAsync<InteractionDto>(caller, contentId, cancellationToken);

        if (who.Failure is { } failure)
        {
            return failure;
        }

        return Result<InteractionDto>.Ok(await _activity.SetInteractionAsync(
            contentId, who.UserId, command.Liked, command.Rating, cancellationToken));
    }

    public async Task<Result> RemoveInteractionAsync(
        int contentId, Caller caller, CancellationToken cancellationToken = default)
    {
        var who = await ResolveAsync<InteractionDto>(caller, contentId, cancellationToken);

        if (who.Failure is { } failure)
        {
            return new Result(failure.Status, failure.Error);
        }

        return await _activity.RemoveInteractionAsync(contentId, who.UserId, cancellationToken)
            ? Result.Ok()
            : Result.NotFound();
    }

    public async Task<Result<CommentDto>> AddCommentAsync(
        int contentId, string? text, Caller caller, CancellationToken cancellationToken = default)
    {
        var problem = Comment.Validate(text);

        if (problem is not null)
        {
            return Result<CommentDto>.Invalid(problem);
        }

        var who = await ResolveAsync<CommentDto>(caller, contentId, cancellationToken);

        if (who.Failure is { } failure)
        {
            return failure;
        }

        return Result<CommentDto>.Ok(await _activity.AddCommentAsync(
            contentId, who.UserId, text!.Trim(), cancellationToken));
    }

    public async Task<Result<WatchDto>> LogWatchAsync(
        int contentId, WatchCommand command, Caller caller, CancellationToken cancellationToken = default)
    {
        if (command.Timestamp is { } timestamp && (float.IsNaN(timestamp) || timestamp < 0 || timestamp > MaxTimestamp))
        {
            return Result<WatchDto>.Invalid("Timestamp must be a positive number.");
        }

        var who = await ResolveAsync<WatchDto>(caller, contentId, cancellationToken);

        if (who.Failure is { } failure)
        {
            return failure;
        }

        var now = DateTime.SpecifyKind(_time.GetUtcNow().UtcDateTime, DateTimeKind.Unspecified);

        return Result<WatchDto>.Ok(await _activity.LogWatchAsync(
            contentId, who.UserId, command.Timestamp, now, cancellationToken));
    }

    public Task<Result<IReadOnlyList<WatchDto>>> GetHistoryAsync(Caller caller, CancellationToken cancellationToken = default) =>
        ForUserAsync(caller, userId => _activity.GetHistoryAsync(userId, HistoryLimit, cancellationToken), cancellationToken);

    public Task<Result<IReadOnlyList<RecommendationDto>>> GetRecommendationsAsync(
        Caller caller, CancellationToken cancellationToken = default) =>
        ForUserAsync(caller, userId => _activity.GetRecommendationsAsync(userId, cancellationToken), cancellationToken);

    public Task<Result<IReadOnlyList<NotificationDto>>> GetNotificationsAsync(
        Caller caller, CancellationToken cancellationToken = default) =>
        ForUserAsync(caller, userId => _activity.GetNotificationsAsync(userId, HistoryLimit, cancellationToken), cancellationToken);

    private async Task<Result<T>> ForUserAsync<T>(
        Caller caller, Func<int, Task<T>> load, CancellationToken cancellationToken)
    {
        if (!caller.IsAuthenticated)
        {
            return Result<T>.Unauthorized();
        }

        var userId = await _accounts.FindUserIdAsync(caller.UserId!, cancellationToken);

        return userId is null
            ? Result<T>.Conflict(StreamingAccountService.ProfileRequired)
            : Result<T>.Ok(await load(userId.Value));
    }

    private async Task<(int UserId, Result<T>? Failure)> ResolveAsync<T>(
        Caller caller, int contentId, CancellationToken cancellationToken)
    {
        if (!caller.IsAuthenticated)
        {
            return (0, Result<T>.Unauthorized());
        }

        if (!await _catalog.ContentExistsAsync(contentId, cancellationToken))
        {
            return (0, Result<T>.NotFound());
        }

        var userId = await _accounts.FindUserIdAsync(caller.UserId!, cancellationToken);

        return userId is null
            ? (0, Result<T>.Conflict(StreamingAccountService.ProfileRequired))
            : (userId.Value, null);
    }
}
