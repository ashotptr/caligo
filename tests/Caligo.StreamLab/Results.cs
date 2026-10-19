using System.Reflection;
using ClickHouse.Client.ADO;
using ClickHouse.Client.Copy;

namespace Caligo.StreamLab;

public sealed record RunRecord(
    Guid RunId,
    string Scenario,
    string Profile,
    DateTime StartedAt,
    DateTime FinishedAt,
    int Listeners,
    int Seconds,
    int Repetitions,
    int Contestants,
    double? BiasMs,
    string Notes);

public sealed record WindowRecord(
    Guid RunId,
    Contestant Contestant,
    int Repetition,
    DateTime StartedAt,
    DateTime FinishedAt,
    long Packets,
    long Lost,
    long Overflowed);

public sealed record ResultRecord(
    Guid RunId,
    string Contestant,
    int Repetition,
    int Listener,
    bool Probe,
    DateTime RecordedAt,
    bool Connected,
    double? TtfbMs,
    double? StartupMs,
    int Stalls,
    double StallMs,
    double MediaMs,
    long Bytes,
    double? LatencyMs,
    double? DataLatencyMs,
    string Error);

public sealed record LatencyRecord(
    Guid RunId,
    string Contestant,
    int Repetition,
    DateTime MeasuredAt,
    double LatencyMs,
    double? DataLatencyMs);

public sealed class ResultStore
{
    private readonly string _connectionString;

    private ResultStore(string connectionString)
    {
        _connectionString = connectionString;
    }

    public static async Task<ResultStore> OpenAsync(string connectionString)
    {
        var store = new ResultStore(connectionString);
        await using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("lab-schema.sql")
                                 ?? throw new InvalidOperationException("the lab schema is not embedded");
        using var reader = new StreamReader(stream);
        var script = await reader.ReadToEndAsync();

        using var connection = new ClickHouseConnection(connectionString);

        foreach (var statement in script.Split(';').Select(text => text.Trim()).Where(text => text.Length > 0))
        {
            using var command = connection.CreateCommand();

            command.CommandText = statement;

            await command.ExecuteNonQueryAsync();
        }

        return store;
    }

    public Task WriteRunAsync(RunRecord run) => InsertAsync(
        "lab_runs",
        new[] { "run_id", "scenario", "profile", "started_at", "finished_at", "listeners", "seconds", "repetitions",
            "contestants", "source_bias_ms", "notes" },
        new[]
        {
            new object?[]
            {
                run.RunId, run.Scenario, run.Profile, run.StartedAt, run.FinishedAt, (ushort)run.Listeners, (ushort)run.Seconds,
                (byte)run.Repetitions, (ushort)run.Contestants, run.BiasMs is { } bias ? (float)bias : null, run.Notes
            }
        });

    public Task WriteWindowAsync(WindowRecord window) => InsertAsync(
        "lab_windows",
        new[] { "run_id", "contestant", "server", "protocol", "container", "repetition", "started_at", "finished_at",
            "impaired_packets", "impaired_lost", "impaired_overflowed" },
        new[]
        {
            new object?[]
            {
                window.RunId, window.Contestant.Name, window.Contestant.Server, window.Contestant.Protocol,
                window.Contestant.Container, (byte)window.Repetition, window.StartedAt, window.FinishedAt,
                (ulong)Math.Max(0, window.Packets), (ulong)Math.Max(0, window.Lost), (ulong)Math.Max(0, window.Overflowed)
            }
        });

    public Task WriteResultsAsync(IEnumerable<ResultRecord> results) => InsertAsync(
        "lab_results",
        new[] { "run_id", "contestant", "repetition", "listener", "probe", "recorded_at", "connected", "ttfb_ms", "startup_ms",
            "stalls", "stall_ms", "media_ms", "bytes", "latency_ms", "data_latency_ms", "error" },
        results.Select(result => new object?[]
        {
            result.RunId, result.Contestant, (byte)result.Repetition, (ushort)result.Listener, result.Probe, result.RecordedAt,
            result.Connected, Single(result.TtfbMs), Single(result.StartupMs), (ushort)Math.Clamp(result.Stalls, 0, ushort.MaxValue),
            (float)result.StallMs, (float)result.MediaMs, (ulong)Math.Max(0, result.Bytes), Single(result.LatencyMs),
            Single(result.DataLatencyMs), result.Error
        }).ToList());

    public Task WriteLatencyAsync(IEnumerable<LatencyRecord> samples) => InsertAsync(
        "lab_latency",
        new[] { "run_id", "contestant", "repetition", "measured_at", "latency_ms", "data_latency_ms" },
        samples.Select(sample => new object?[]
        {
            sample.RunId, sample.Contestant, (byte)sample.Repetition, sample.MeasuredAt, (float)sample.LatencyMs,
            Single(sample.DataLatencyMs)
        }).ToList());

    private static object? Single(double? value) => value is { } number && double.IsFinite(number) ? (float)number : null;

    private async Task InsertAsync(string table, string[] columns, IReadOnlyCollection<object?[]> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        using var connection = new ClickHouseConnection(_connectionString);
        using var bulk = new ClickHouseBulkCopy(connection)
        {
            DestinationTableName = table,
            ColumnNames = columns,
            BatchSize = rows.Count,
            MaxDegreeOfParallelism = 1
        };

        await bulk.InitAsync();
        await bulk.WriteToServerAsync(rows.Select(row => (object[])row!));
    }
}
