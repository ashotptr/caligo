namespace Caligo.Analytics.Core.Playback;

public class PlaybackEventRow
{
    public string EventId { get; set; } = string.Empty;

    public string DeviceId { get; set; } = string.Empty;

    public long Seq { get; set; }

    public string SessionId { get; set; } = string.Empty;

    public int ContentId { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string Delivery { get; set; } = string.Empty;

    public DateTime OccurredAt { get; set; }

    public float Position { get; set; }

    public int DurationMs { get; set; }

    public int FromHeight { get; set; }

    public int ToHeight { get; set; }

    public string Detail { get; set; } = string.Empty;
}

public record PlaybackSummary(
    int ContentId,
    long Sessions,
    long Startups,
    double MedianStartupMs,
    double P95StartupMs,
    long Stalls,
    long StallMs,
    long QualitySwitches,
    long Errors,
    long HlsStartups,
    long NativeHlsStartups,
    long DashStartups,
    long ProgressiveStartups);

public interface IPlaybackStore
{
    bool IsConfigured { get; }

    Task WriteAsync(IReadOnlyList<PlaybackEventRow> events, string batchId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PlaybackSummary>> SummaryAsync(int days, int limit, CancellationToken cancellationToken = default);
}
