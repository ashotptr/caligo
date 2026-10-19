using Caligo.Analytics.Core.Views;

namespace Caligo.Analytics.Core.Olap;

public class ClapEvent
{
    public string EventId { get; set; } = string.Empty;

    public int PostId { get; set; }

    public string UserId { get; set; } = string.Empty;

    public int Delta { get; set; }

    public bool FirstClap { get; set; }

    public DateTime ClappedAt { get; set; }
}

public enum SeriesBucket
{
    Minute,
    Hour,
    Day
}

public enum RankBy
{
    Views,
    Claps
}

public record SeriesPoint(DateTime BucketStart, long Views, long UniqueVisitors, long Claps);

public record WindowEntry(int PostId, long Views, long UniqueVisitors, long Claps);

public record EngagementFunnel(int PostId, long Views, long UniqueVisitors, long Clappers, long Claps);

public interface IOlapSink
{
    bool IsConfigured { get; }

    Task WriteViewsAsync(IReadOnlyList<PostView> views, string batchId, CancellationToken cancellationToken = default);

    Task WriteClapsAsync(IReadOnlyList<ClapEvent> claps, string batchId, CancellationToken cancellationToken = default);
}

public interface IOlapQueries
{
    bool IsConfigured { get; }

    Task<IReadOnlyList<SeriesPoint>> SeriesAsync(
        int postId, DateTime from, DateTime to, SeriesBucket bucket, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WindowEntry>> TopInWindowAsync(
        int minutes, RankBy rankBy, int limit, CancellationToken cancellationToken = default);

    Task<EngagementFunnel> FunnelAsync(int postId, int days, CancellationToken cancellationToken = default);
}
