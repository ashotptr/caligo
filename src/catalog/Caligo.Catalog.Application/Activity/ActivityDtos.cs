namespace Caligo.Catalog.Application.Activity;

public record InteractionDto(int ContentId, bool? Liked, float? Rating, float? AverageRating);

public record SetInteractionCommand(bool? Liked, float? Rating);

public record WatchCommand(float? Timestamp);

public record WatchDto(int WatchedContentId, int ContentId, string Title, string Kind, float? Timestamp, DateTime Date);

public record RecommendationDto(int ContentId, string Title, string Kind, string? Reason, DateTime? Date);

public record NotificationDto(int NotificationId, string Reason, DateTime Date);
