using System.Data.Common;
using ClickHouse.Client.ADO;
using ClickHouse.Client.Utility;
using Microsoft.Extensions.Options;
using Caligo.Analytics.Core.Lab;

namespace Caligo.Analytics.Infrastructure.Stores
{
    public class ClickHouseLabStore : ILabStore
    {
        private const int MaxLatencyPoints = 20000;

        private readonly ClickHouseOptions _options;

        public ClickHouseLabStore(IOptions<ClickHouseOptions> options)
        {
            _options = options.Value;
        }

        public bool IsConfigured => _options.Enabled;

        public Task<IReadOnlyList<LabRunSummary>> RunsAsync(int limit, CancellationToken cancellationToken = default) =>
            QueryAsync(
                """
                SELECT run_id, scenario, profile, started_at, finished_at, listeners, seconds, repetitions, contestants,
                       source_bias_ms, notes
                FROM lab_runs FINAL
                ORDER BY started_at DESC
                LIMIT {limit:UInt32}
                """,
                command => command.AddParameter("limit", (uint)Math.Clamp(limit, 1, 500)),
                ReadRun,
                cancellationToken);

        public async Task<LabRunDetail?> RunAsync(Guid runId, CancellationToken cancellationToken = default)
        {
            var runs = await QueryAsync(
                """
                SELECT run_id, scenario, profile, started_at, finished_at, listeners, seconds, repetitions, contestants,
                       source_bias_ms, notes
                FROM lab_runs FINAL
                WHERE run_id = {run:UUID}
                """,
                command => command.AddParameter("run", runId),
                ReadRun,
                cancellationToken);

            if (runs.Count == 0)
            {
                return null;
            }

            var listeners = await QueryAsync(
                """
                SELECT contestant,
                       uniqExact(repetition),
                       count(),
                       countIf(connected),
                       quantileExact(0.5)(ttfb_ms),
                       quantileExact(0.5)(startup_ms),
                       quantileExact(0.95)(startup_ms),
                       sum(stalls),
                       sum(stall_ms),
                       sum(media_ms),
                       quantileExact(0.5)(latency_ms),
                       quantileExact(0.5)(data_latency_ms),
                       arrayStringConcat(arraySlice(groupUniqArrayIf(error, error != ''), 1, 3), ' | ')
                FROM lab_results
                WHERE run_id = {run:UUID}
                GROUP BY contestant
                """,
                command => command.AddParameter("run", runId),
                reader => (Contestant: reader.GetString(0), Values: Enumerable.Range(1, 12).Select(reader.GetValue).ToArray()),
                cancellationToken);

            var spreads = (await QueryAsync(
                """
                SELECT contestant, max(startup) - min(startup), max(latency) - min(latency)
                FROM
                (
                    SELECT contestant, repetition, quantileExact(0.5)(startup_ms) AS startup, quantileExact(0.5)(latency_ms) AS latency
                    FROM lab_results
                    WHERE run_id = {run:UUID}
                    GROUP BY contestant, repetition
                )
                GROUP BY contestant
                """,
                command => command.AddParameter("run", runId),
                reader => (Contestant: reader.GetString(0), Startup: Optional(reader.GetValue(1)), Latency: Optional(reader.GetValue(2))),
                cancellationToken)).ToDictionary(row => row.Contestant);

            var windows = (await QueryAsync(
                """
                SELECT contestant, any(server), any(protocol), any(container),
                       sum(impaired_packets), sum(impaired_lost), sum(impaired_overflowed)
                FROM lab_windows
                WHERE run_id = {run:UUID}
                GROUP BY contestant
                """,
                command => command.AddParameter("run", runId),
                reader => (Contestant: reader.GetString(0), Server: reader.GetString(1), Protocol: reader.GetString(2),
                    Container: reader.GetString(3), Packets: Whole(reader.GetValue(4)), Lost: Whole(reader.GetValue(5)),
                    Overflowed: Whole(reader.GetValue(6))),
                cancellationToken)).ToDictionary(row => row.Contestant);

            var resources = (await QueryAsync(
                """
                SELECT w.contestant, avg(s.cpu_percent), max(s.cpu_percent), max(s.memory_bytes)
                FROM lab_windows AS w
                INNER JOIN lab_resources AS s ON s.run_id = w.run_id AND s.container = w.container
                WHERE w.run_id = {run:UUID} AND s.sampled_at >= w.started_at AND s.sampled_at <= w.finished_at
                GROUP BY w.contestant
                """,
                command => command.AddParameter("run", runId),
                reader => (Contestant: reader.GetString(0), Mean: Optional(reader.GetValue(1)), Peak: Optional(reader.GetValue(2)),
                    Memory: Optional(reader.GetValue(3))),
                cancellationToken)).ToDictionary(row => row.Contestant);

            var latency = await QueryAsync(
                """
                SELECT contestant, repetition, toStartOfSecond(measured_at) AS second, quantileExact(0.5)(latency_ms)
                FROM lab_latency
                WHERE run_id = {run:UUID}
                GROUP BY contestant, repetition, second
                ORDER BY second
                LIMIT {limit:UInt32}
                """,
                command =>
                {
                    command.AddParameter("run", runId);
                    command.AddParameter("limit", (uint)MaxLatencyPoints);
                },
                reader => new LabLatencyPoint(reader.GetString(0), Convert.ToInt32(reader.GetValue(1)),
                    DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc), Real(reader.GetValue(3))),
                cancellationToken);

            var contestants = listeners.Select(row =>
            {
                var values = row.Values;
                windows.TryGetValue(row.Contestant, out var window);
                spreads.TryGetValue(row.Contestant, out var spread);
                resources.TryGetValue(row.Contestant, out var resource);

                return new LabContestantResult(
                    row.Contestant,
                    window.Server ?? string.Empty,
                    window.Protocol ?? string.Empty,
                    window.Container ?? string.Empty,
                    Convert.ToInt32(values[0]),
                    Whole(values[1]),
                    Whole(values[2]),
                    Optional(values[3]),
                    Optional(values[4]),
                    Optional(values[5]),
                    spread.Startup,
                    Whole(values[6]),
                    Real(values[7]),
                    Real(values[8]),
                    Optional(values[9]),
                    spread.Latency,
                    Optional(values[10]),
                    resource.Mean,
                    resource.Peak,
                    resource.Memory,
                    window.Packets,
                    window.Lost,
                    window.Overflowed,
                    values[11] as string ?? string.Empty);
            }).OrderBy(result => result.Contestant, StringComparer.Ordinal).ToList();

            return new LabRunDetail(runs[0], contestants, latency);
        }

        private static LabRunSummary ReadRun(DbDataReader reader) => new(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc),
            DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc),
            Convert.ToInt32(reader.GetValue(5)),
            Convert.ToInt32(reader.GetValue(6)),
            Convert.ToInt32(reader.GetValue(7)),
            Convert.ToInt32(reader.GetValue(8)),
            Optional(reader.GetValue(9)),
            reader.GetString(10));

        private async Task<IReadOnlyList<T>> QueryAsync<T>(
            string sql, Action<ClickHouseCommand> bind, Func<DbDataReader, T> read, CancellationToken cancellationToken)
        {
            using var connection = Connect();
            using var command = connection.CreateCommand();

            command.CommandText = sql;
            bind(command);

            using var reader = await command.ExecuteReaderAsync(cancellationToken);

            var rows = new List<T>();

            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(read(reader));
            }

            return rows;
        }

        private ClickHouseConnection Connect()
        {
            if (!_options.Enabled)
            {
                throw new InvalidOperationException("ConnectionStrings:ClickHouse is not configured.");
            }

            return new ClickHouseConnection(_options.ConnectionString);
        }

        private static long Whole(object value) => value is DBNull or null ? 0 : Convert.ToInt64(value);

        private static double Real(object value)
        {
            var number = value is DBNull or null ? 0 : Convert.ToDouble(value);

            return double.IsFinite(number) ? number : 0;
        }

        private static double? Optional(object value)
        {
            if (value is DBNull or null)
            {
                return null;
            }

            var number = Convert.ToDouble(value);

            return double.IsFinite(number) ? number : null;
        }
    }
}
