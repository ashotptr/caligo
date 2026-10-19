using ClickHouse.Client.ADO;
using ClickHouse.Client.Copy;
using ClickHouse.Client.Utility;
using Microsoft.Extensions.Options;
using Caligo.Analytics.Core.Playback;

namespace Caligo.Analytics.Infrastructure.Stores
{
    public class ClickHousePlaybackStore : IPlaybackStore
    {
        private static readonly string[] Columns =
        {
            "event_id", "device_id", "seq", "session_id", "content_id", "kind", "delivery",
            "occurred_at", "position", "duration_ms", "from_height", "to_height", "detail"
        };

        private readonly ClickHouseOptions _options;

        public ClickHousePlaybackStore(IOptions<ClickHouseOptions> options)
        {
            _options = options.Value;
        }

        public bool IsConfigured => _options.Enabled;

        public async Task WriteAsync(
            IReadOnlyList<PlaybackEventRow> events, string batchId, CancellationToken cancellationToken = default)
        {
            if (events.Count == 0)
            {
                return;
            }

            using var connection = Connect();

            connection.CustomSettings["insert_deduplication_token"] = batchId;

            using var bulk = new ClickHouseBulkCopy(connection)
            {
                DestinationTableName = "playback_events",
                ColumnNames = Columns,
                BatchSize = events.Count,
                MaxDegreeOfParallelism = 1
            };

            await bulk.InitAsync();
            await bulk.WriteToServerAsync(
                events.Select(e => new object[]
                {
                    Uuid(e.EventId),
                    e.DeviceId,
                    (ulong)Math.Max(e.Seq, 0),
                    Uuid(e.SessionId),
                    e.ContentId,
                    e.Kind,
                    e.Delivery,
                    Utc(e.OccurredAt),
                    e.Position,
                    (uint)Math.Max(e.DurationMs, 0),
                    (ushort)Math.Clamp(e.FromHeight, 0, ushort.MaxValue),
                    (ushort)Math.Clamp(e.ToHeight, 0, ushort.MaxValue),
                    e.Detail
                }).ToList(),
                cancellationToken);
        }

        public async Task<IReadOnlyList<PlaybackSummary>> SummaryAsync(
            int days, int limit, CancellationToken cancellationToken = default)
        {
            const string sql = """
                SELECT content_id,
                       uniqExact(session_id),
                       countIf(kind = 'startup'),
                       quantileExactIf(0.5)(duration_ms, kind = 'startup'),
                       quantileExactIf(0.95)(duration_ms, kind = 'startup'),
                       countIf(kind = 'stall'),
                       sumIf(duration_ms, kind = 'stall'),
                       countIf(kind = 'quality'),
                       countIf(kind = 'error'),
                       countIf(kind = 'startup' AND delivery = 'hls'),
                       countIf(kind = 'startup' AND delivery = 'hls-native'),
                       countIf(kind = 'startup' AND delivery = 'dash'),
                       countIf(kind = 'startup' AND delivery = 'progressive')
                FROM playback_events FINAL
                WHERE occurred_at >= {since:DateTime}
                GROUP BY content_id
                ORDER BY uniqExact(session_id) DESC, content_id
                LIMIT {limit:UInt32}
                """;

            using var connection = Connect();
            using var command = connection.CreateCommand();

            command.CommandText = sql;
            command.AddParameter("since", DateTime.UtcNow.AddDays(-days));
            command.AddParameter("limit", (uint)Math.Max(limit, 1));

            using var reader = await command.ExecuteReaderAsync(cancellationToken);

            var rows = new List<PlaybackSummary>();

            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new PlaybackSummary(
                    reader.GetInt32(0),
                    Whole(reader.GetValue(1)),
                    Whole(reader.GetValue(2)),
                    Real(reader.GetValue(3)),
                    Real(reader.GetValue(4)),
                    Whole(reader.GetValue(5)),
                    Whole(reader.GetValue(6)),
                    Whole(reader.GetValue(7)),
                    Whole(reader.GetValue(8)),
                    Whole(reader.GetValue(9)),
                    Whole(reader.GetValue(10)),
                    Whole(reader.GetValue(11)),
                    Whole(reader.GetValue(12))));
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

        private static Guid Uuid(string value) => Guid.TryParse(value, out var id) ? id : Guid.Empty;

        private static long Whole(object value) => value is DBNull ? 0 : Convert.ToInt64(value);

        private static double Real(object value)
        {
            var number = value is DBNull ? 0 : Convert.ToDouble(value);

            return double.IsFinite(number) ? number : 0;
        }

        private static DateTime Utc(DateTime value) =>
            value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc);
    }
}
