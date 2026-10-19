using Caligo.Catalog.Application.Activity;
using Caligo.Catalog.Application.Contents;

namespace Caligo.Catalog.Application.Abstractions;

public interface IActivityStore
{
    Task<InteractionDto?> GetInteractionAsync(int contentId, int userId, CancellationToken cancellationToken = default);

    Task<InteractionDto> SetInteractionAsync(
        int contentId, int userId, bool? liked, float? rating, CancellationToken cancellationToken = default);

    Task<bool> RemoveInteractionAsync(int contentId, int userId, CancellationToken cancellationToken = default);

    Task<CommentDto> AddCommentAsync(int contentId, int userId, string text, CancellationToken cancellationToken = default);

    Task<WatchDto> LogWatchAsync(
        int contentId, int userId, float? timestamp, DateTime date, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WatchDto>> GetHistoryAsync(int userId, int limit, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RecommendationDto>> GetRecommendationsAsync(int userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NotificationDto>> GetNotificationsAsync(int userId, int limit, CancellationToken cancellationToken = default);
}
