using Grpc.Core;
using Microsoft.Extensions.Options;
using Caligo.Contracts.Analytics;
using Caligo.Posts.Infrastructure.Messaging;
using Caligo.SharedKernel;

namespace Caligo.Api.Lab;

public record LabRunDto(
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

public record LabContestantDto(
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
    double? StallsPerListenerMinute,
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

public record LabLatencyPointDto(string Contestant, int Repetition, DateTime MeasuredAt, double LatencyMs);

public record LabRunsDto(bool Enabled, IReadOnlyList<LabRunDto> Runs);

public record LabRunDetailDto(
    bool Enabled,
    LabRunDto Run,
    IReadOnlyList<LabContestantDto> Contestants,
    IReadOnlyList<LabLatencyPointDto> Latency);

public class LabResults
{
    public const int DefaultLimit = 50;

    public const string UnavailableMessage = "Lab results are unavailable right now. Try again in a moment.";

    private readonly AnalyticsReader.AnalyticsReaderClient _analytics;
    private readonly AnalyticsClientOptions _options;
    private readonly ILogger<LabResults> _logger;

    public LabResults(
        AnalyticsReader.AnalyticsReaderClient analytics,
        IOptions<AnalyticsClientOptions> options,
        ILogger<LabResults> logger)
    {
        _analytics = analytics;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<Result<LabRunsDto>> RunsAsync(int limit, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return Result<LabRunsDto>.Ok(new LabRunsDto(false, Array.Empty<LabRunDto>()));
        }

        try
        {
            var reply = await _analytics.GetLabRunsAsync(
                new GetLabRunsRequest { Limit = Math.Clamp(limit, 1, 200) },
                new CallOptions(deadline: DateTime.UtcNow.Add(_options.LabDeadline), cancellationToken: cancellationToken));

            return Result<LabRunsDto>.Ok(new LabRunsDto(reply.Enabled, reply.Runs.Select(ToDto).ToList()));
        }
        catch (RpcException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Lab runs could not be read from the analytics service.");

            return Result<LabRunsDto>.Unavailable(UnavailableMessage);
        }
    }

    public async Task<Result<LabRunDetailDto>> RunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return Result<LabRunDetailDto>.NotFound();
        }

        LabRunDetail reply;

        try
        {
            reply = await _analytics.GetLabRunAsync(
                new GetLabRunRequest { RunId = runId.ToString() },
                new CallOptions(deadline: DateTime.UtcNow.Add(_options.LabDeadline), cancellationToken: cancellationToken));
        }
        catch (RpcException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Lab run {RunId} could not be read from the analytics service.", runId);

            return Result<LabRunDetailDto>.Unavailable(UnavailableMessage);
        }

        if (!reply.Found)
        {
            return Result<LabRunDetailDto>.NotFound();
        }

        return Result<LabRunDetailDto>.Ok(new LabRunDetailDto(
            reply.Enabled,
            ToDto(reply.Run),
            reply.Contestants.Select(result => new LabContestantDto(
                result.Contestant,
                result.Server,
                result.Protocol,
                result.Container,
                result.Repetitions,
                result.Listeners,
                result.Connected,
                result.TtfbP50Ms,
                result.StartupP50Ms,
                result.StartupP95Ms,
                result.StartupSpreadMs,
                result.Stalls,
                result.StallMs,
                result.MediaMs > 0 ? Math.Round(result.Stalls / (result.MediaMs / 60000.0), 3) : null,
                result.MediaMs,
                result.LatencyP50Ms,
                result.LatencySpreadMs,
                result.DataLatencyP50Ms,
                result.CpuMeanPercent,
                result.CpuPeakPercent,
                result.MemoryPeakBytes,
                result.ImpairedPackets,
                result.ImpairedLost,
                result.ImpairedOverflowed,
                result.Errors)).ToList(),
            reply.Latency.Select(point => new LabLatencyPointDto(
                point.Contestant, point.Repetition, point.MeasuredAt.ToDateTime(), point.LatencyMs)).ToList()));
    }

    private static LabRunDto ToDto(LabRun run) => new(
        Guid.TryParse(run.RunId, out var id) ? id : Guid.Empty,
        run.Scenario,
        run.Profile,
        run.StartedAt.ToDateTime(),
        run.FinishedAt.ToDateTime(),
        run.Listeners,
        run.Seconds,
        run.Repetitions,
        run.Contestants,
        run.SourceBiasMs,
        run.Notes);
}
