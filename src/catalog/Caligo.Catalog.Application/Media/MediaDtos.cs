namespace Caligo.Catalog.Application.Media;

public static class MediaFormats
{
    public const string Hls = "hls";
    public const string Dash = "dash";
    public const string Progressive = "progressive";
}

public record RenditionDto(string Kind, string Codec, int Bitrate, int? Width, int? Height, string? Language);

public record SubtitleDto(string Label, string? Language);

public record MediaInfoDto(
    int ContentId,
    string Status,
    bool Playable,
    float? DurationSeconds,
    DateTime? PackagedAt,
    IReadOnlyList<string> Formats,
    IReadOnlyList<RenditionDto> Renditions,
    IReadOnlyList<SubtitleDto> Subtitles,
    string? SourceFile,
    string? Error);

public record InboxSubtitleDto(string File, string Language, bool Known);

public record InboxFileDto(string File, long Size, DateTime Modified, IReadOnlyList<InboxSubtitleDto> Subtitles);

public record StartSessionCommand(int ContentId, string? DeviceId);

public record PositionCommand(float Position, long Seq);

public record CompleteCommand(Guid CompletionId, float? Position);

public record SubtitleTrackDto(string Label, string? Language, string Url);

public record PlaybackSessionDto(
    Guid SessionId,
    int ContentId,
    DateTimeOffset ExpiresAt,
    int LeaseSeconds,
    int HeartbeatSeconds,
    float Position,
    float? DurationSeconds,
    string? PlaylistUrl,
    string? ManifestUrl,
    string? ProgressiveUrl,
    IReadOnlyList<SubtitleTrackDto> Subtitles,
    IReadOnlyList<RenditionDto> Renditions,
    int MaxStreams,
    bool Resumed);

public record PositionDto(float Position, long Seq, DateTimeOffset ExpiresAt, int LeaseSeconds);

public record CompletionDto(
    Guid CompletionId, int ContentId, int? WatchedContentId, DateTimeOffset CompletedAt, bool Repeated);

public record SessionAccessDto(Guid SessionId, long LiveUntil, string StoragePath);

public static class PlaybackEventTypes
{
    public const string Startup = "startup";
    public const string Stall = "stall";
    public const string Quality = "quality";
    public const string Error = "error";
    public const string Play = "play";
    public const string Pause = "pause";
    public const string Seek = "seek";
    public const string Ended = "ended";

    public static bool IsKnown(string? type) =>
        type is Startup or Stall or Quality or Error or Play or Pause or Seek or Ended;
}

public static class PlaybackDeliveries
{
    public const string Hls = "hls";
    public const string NativeHls = "hls-native";
    public const string Dash = "dash";
    public const string Progressive = "progressive";

    public static bool IsKnown(string? delivery) => delivery is Hls or NativeHls or Dash or Progressive;
}

public record PlaybackEventCommand(
    Guid EventId,
    long Seq,
    string? Type,
    long? At,
    float? Position,
    int? DurationMs,
    int? From,
    int? To,
    string? Delivery,
    string? Detail);

public record PlaybackEventRecord(
    Guid EventId,
    string DeviceId,
    long Seq,
    Guid SessionId,
    int ContentId,
    string Type,
    string Delivery,
    DateTime OccurredAt,
    float Position,
    int DurationMs,
    int FromHeight,
    int ToHeight,
    string Detail);

public record EventsAcceptedDto(int Accepted, int Rejected);
