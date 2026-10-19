namespace Blog.Analytics.Core.Lab;

public record LabRunSummary(
    Guid RunId,
    string Scenario,
    string Profile,
    DateTime StartedAt,
    DateTime FinishedAt,
    int Listeners,
    int Seconds,
    int Repetitions,
    int Contestants,
    double? SourceBiasMs,
    string Notes);

public record LabContestantResult(
    string Contestant,
    string Server,
    string Protocol,
    string Container,
    int Repetitions,
    long Listeners,
    long Connected,
    double? TtfbP50Ms,
    double? StartupP50Ms,
    double? StartupP95Ms,
    double? StartupSpreadMs,
    long Stalls,
    double StallMs,
    double MediaMs,
    double? LatencyP50Ms,
    double? LatencySpreadMs,
    double? DataLatencyP50Ms,
    double? CpuMeanPercent,
    double? CpuPeakPercent,
    double? MemoryPeakBytes,
    long ImpairedPackets,
    long ImpairedLost,
    long ImpairedOverflowed,
    string Errors);

public record LabLatencyPoint(string Contestant, int Repetition, DateTime MeasuredAt, double LatencyMs);

public record LabRunDetail(
    LabRunSummary Run,
    IReadOnlyList<LabContestantResult> Contestants,
    IReadOnlyList<LabLatencyPoint> Latency);

public interface ILabStore
{
    bool IsConfigured { get; }

    Task<IReadOnlyList<LabRunSummary>> RunsAsync(int limit, CancellationToken cancellationToken = default);

    Task<LabRunDetail?> RunAsync(Guid runId, CancellationToken cancellationToken = default);
}
