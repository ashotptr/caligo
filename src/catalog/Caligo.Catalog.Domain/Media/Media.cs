namespace Caligo.Catalog.Domain.Media;

public static class MediaStatus
{
    public const string Queued = "queued";
    public const string Packaging = "packaging";
    public const string Ready = "ready";
    public const string Failed = "failed";
}

public static class RenditionKinds
{
    public const string Video = "video";
    public const string Audio = "audio";
    public const string Progressive = "progressive";
}

public class ContentMedia
{
    public const int MaxSourceFileLength = 255;
    public const int MaxErrorLength = 2000;

    public int ContentId { get; set; }

    public string Status { get; set; } = MediaStatus.Queued;

    public string SourceFile { get; set; } = string.Empty;

    public DateTime RequestedAt { get; set; }

    public DateTime? ClaimedUntil { get; set; }

    public int Attempts { get; set; }

    public DateTime? PackagedAt { get; set; }

    public string? StoragePath { get; set; }

    public string? HlsPath { get; set; }

    public string? DashPath { get; set; }

    public float? DurationSeconds { get; set; }

    public string? Error { get; set; }

    public bool IsPlayable => !string.IsNullOrEmpty(StoragePath);

    public bool IsBusy => Status is MediaStatus.Queued or MediaStatus.Packaging;
}

public class Rendition
{
    public int RenditionId { get; set; }

    public int ContentId { get; set; }

    public string Kind { get; set; } = RenditionKinds.Video;

    public string Codec { get; set; } = string.Empty;

    public int Bitrate { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    public string? Language { get; set; }

    public string Path { get; set; } = string.Empty;
}

public class SubtitleFile
{
    public const string WebVtt = "vtt";

    public int SubtitleId { get; set; }

    public string Uri { get; set; } = string.Empty;

    public string Format { get; set; } = WebVtt;
}

public class PlaybackSession
{
    public const int MinDeviceIdLength = 8;
    public const int MaxDeviceIdLength = 64;

    public Guid SessionId { get; set; }

    public int UserId { get; set; }

    public int ContentId { get; set; }

    public string DeviceId { get; set; } = string.Empty;

    public DateTime StartedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    public DateTime? EndedAt { get; set; }

    public Guid? CompletionId { get; set; }

    public DateTime? CompletedAt { get; set; }

    public int? WatchedContentId { get; set; }

    public bool IsCompleted => CompletionId is not null;

    public bool IsLiveAt(DateTime now) => EndedAt is null && ExpiresAt > now;

    public static bool IsValidDeviceId(string? deviceId)
    {
        if (deviceId is null || deviceId.Length < MinDeviceIdLength || deviceId.Length > MaxDeviceIdLength)
        {
            return false;
        }

        foreach (var c in deviceId)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')
            {
                return false;
            }
        }

        return true;
    }
}

public class PlaybackPosition
{
    public const float MaxPosition = 1_000_000f;

    public int UserId { get; set; }

    public int ContentId { get; set; }

    public float Position { get; set; }

    public Guid? SessionId { get; set; }

    public long Seq { get; set; }

    public DateTime UpdatedAt { get; set; }

    public static bool IsValid(float position) =>
        !float.IsNaN(position) && position >= 0f && position <= MaxPosition;
}
