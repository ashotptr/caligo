using System.Data.Common;
using System.Runtime.ExceptionServices;
using ClickHouse.Client.ADO;
using ClickHouse.Client.Copy;
using ClickHouse.Client.Utility;
using DbUp;
using DbUp.ClickHouse;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Blog.Analytics.Core.Olap;
using Blog.Analytics.Core.Views;

namespace Blog.Analytics.Infrastructure.Stores
{
    public class ClickHouseOptions
    {
        public string ConnectionString { get; set; } = string.Empty;

        public bool Enabled => !string.IsNullOrWhiteSpace(ConnectionString);
    }

    public class ClickHouseOlapStore : IOlapSink, IOlapQueries
    {
        private static readonly string[] ViewColumns =
            { "event_id", "post_id", "viewed_at", "visitor_hash", "user_id", "referrer_host" };

        private static readonly string[] ClapColumns =
            { "event_id", "post_id", "user_id", "delta", "first_clap", "clapped_at" };

        private readonly ClickHouseOptions _options;
        private readonly ILogger<ClickHouseOlapStore> _logger;

        public ClickHouseOlapStore(IOptions<ClickHouseOptions> options, ILogger<ClickHouseOlapStore> logger)
        {
            _options = options.Value;
            _logger = logger;
        }

        public bool IsConfigured => _options.Enabled;

        public Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
        {
            if (!_options.Enabled)
            {
                throw new InvalidOperationException("ConnectionStrings:ClickHouse is not configured.");
            }

            return Task.Run(() =>
            {
                var result = DeployChanges.To
                    .ClickHouseDatabase(_options.ConnectionString)
                    .WithScriptsEmbeddedInAssembly(
                        typeof(ClickHouseOlapStore).Assembly,
                        name => name.StartsWith("ClickHouseMigrations.", StringComparison.Ordinal))
                    .WithoutTransaction()
                    .LogTo(_logger)
                    .Build()
                    .PerformUpgrade();

                if (!result.Successful)
                {
                    ExceptionDispatchInfo.Throw(result.Error);
                }

                _logger.LogInformation(
                    "ClickHouse schema is in place; {Count} new script(s) applied.", result.Scripts.Count());
            }, cancellationToken);
        }

        public Task WriteViewsAsync(
            IReadOnlyList<PostView> views, string batchId, CancellationToken cancellationToken = default) =>
            InsertAsync(
                "post_views",
                ViewColumns,
                views.Select(v => new object[]
                {
                    EventId(v.EventId), v.PostId, v.ViewedAt, v.VisitorHash ?? string.Empty,
                    v.UserId ?? string.Empty, ReferrerHost(v.Referrer)
                }).ToList(),
                batchId,
                cancellationToken);

        public Task WriteClapsAsync(
            IReadOnlyList<ClapEvent> claps, string batchId, CancellationToken cancellationToken = default) =>
            InsertAsync(
                "clap_events",
                ClapColumns,
                claps.Select(c => new object[]
                {
                    EventId(c.EventId), c.PostId, c.UserId, c.Delta, c.FirstClap, c.ClappedAt
                }).ToList(),
                batchId,
                cancellationToken);

        public async Task<IReadOnlyList<SeriesPoint>> SeriesAsync(
            int postId, DateTime from, DateTime to, SeriesBucket bucket, CancellationToken cancellationToken = default)
        {
            var unit = bucket switch
            {
                SeriesBucket.Minute => "MINUTE",
                SeriesBucket.Hour => "HOUR",
                _ => "DAY"
            };

            var sql = $$"""
                SELECT bucket, sum(v) AS views, max(u) AS visitors, sum(c) AS claps
                FROM
                (
                    SELECT toStartOfInterval(minute, INTERVAL 1 {{unit}}) AS bucket,
                           sum(views) AS v, uniqMerge(visitors) AS u, toInt64(0) AS c
                    FROM post_views_1m
                    WHERE post_id = {postId:Int32} AND minute >= {from:DateTime} AND minute < {to:DateTime}
                    GROUP BY bucket
                    UNION ALL
                    SELECT toStartOfInterval(minute, INTERVAL 1 {{unit}}) AS bucket,
                           toUInt64(0), toUInt64(0), sum(claps)
                    FROM claps_1m
                    WHERE post_id = {postId:Int32} AND minute >= {from:DateTime} AND minute < {to:DateTime}
                    GROUP BY bucket
                )
                GROUP BY bucket
                ORDER BY bucket
                """;

            return await QueryAsync(
                sql,
                command =>
                {
                    command.AddParameter("postId", postId);
                    command.AddParameter("from", Utc(from));
                    command.AddParameter("to", Utc(to));
                },
                reader => new SeriesPoint(
                    DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc),
                    Convert.ToInt64(reader.GetValue(1)),
                    Convert.ToInt64(reader.GetValue(2)),
                    Convert.ToInt64(reader.GetValue(3))),
                cancellationToken);
        }

        public async Task<IReadOnlyList<WindowEntry>> TopInWindowAsync(
            int minutes, RankBy rankBy, int limit, CancellationToken cancellationToken = default)
        {
            var order = rankBy == RankBy.Claps ? "claps" : "views";

            var sql = $$"""
                SELECT post_id, sum(v) AS views, max(u) AS visitors, sum(c) AS claps
                FROM
                (
                    SELECT post_id, sum(views) AS v, uniqMerge(visitors) AS u, toInt64(0) AS c
                    FROM post_views_1m
                    WHERE minute >= {since:DateTime}
                    GROUP BY post_id
                    UNION ALL
                    SELECT post_id, toUInt64(0), toUInt64(0), sum(claps)
                    FROM claps_1m
                    WHERE minute >= {since:DateTime}
                    GROUP BY post_id
                )
                GROUP BY post_id
                ORDER BY {{order}} DESC, post_id
                LIMIT {limit:UInt32}
                """;

            return await QueryAsync(
                sql,
                command =>
                {
                    command.AddParameter("since", MinuteFloor(DateTime.UtcNow.AddMinutes(-minutes)));
                    command.AddParameter("limit", (uint)limit);
                },
                reader => new WindowEntry(
                    reader.GetInt32(0),
                    Convert.ToInt64(reader.GetValue(1)),
                    Convert.ToInt64(reader.GetValue(2)),
                    Convert.ToInt64(reader.GetValue(3))),
                cancellationToken);
        }

        public async Task<EngagementFunnel> FunnelAsync(int postId, int days, CancellationToken cancellationToken = default)
        {
            const string sql = """
                SELECT
                    (SELECT sum(views) FROM post_views_1m WHERE post_id = {postId:Int32} AND minute >= {since:DateTime}),
                    (SELECT uniqMerge(visitors) FROM post_views_1m WHERE post_id = {postId:Int32} AND minute >= {since:DateTime}),
                    (SELECT uniqMerge(clappers) FROM claps_1m WHERE post_id = {postId:Int32} AND minute >= {since:DateTime}),
                    (SELECT sum(claps) FROM claps_1m WHERE post_id = {postId:Int32} AND minute >= {since:DateTime})
                """;

            var rows = await QueryAsync(
                sql,
                command =>
                {
                    command.AddParameter("postId", postId);
                    command.AddParameter("since", MinuteFloor(DateTime.UtcNow.AddDays(-days)));
                },
                reader => new EngagementFunnel(
                    postId,
                    ToLong(reader.GetValue(0)),
                    ToLong(reader.GetValue(1)),
                    ToLong(reader.GetValue(2)),
                    ToLong(reader.GetValue(3))),
                cancellationToken);

            return rows.Count == 0 ? new EngagementFunnel(postId, 0, 0, 0, 0) : rows[0];
        }

        private async Task InsertAsync(
            string table, string[] columns, IReadOnlyList<object[]> rows, string batchId, CancellationToken cancellationToken)
        {
            if (rows.Count == 0)
            {
                return;
            }

            using var connection = Connect();

            connection.CustomSettings["insert_deduplication_token"] = batchId;

            using var bulk = new ClickHouseBulkCopy(connection)
            {
                DestinationTableName = table,
                ColumnNames = columns,
                BatchSize = rows.Count,
                MaxDegreeOfParallelism = 1
            };

            await bulk.InitAsync();
            await bulk.WriteToServerAsync(rows, cancellationToken);
        }

        private async Task<IReadOnlyList<T>> QueryAsync<T>(
            string sql, Action<ClickHouseCommand> bind, Func<DbDataReader, T> read, CancellationToken cancellationToken)
        {
            using var connection = Connect();
            using var command = (ClickHouseCommand)connection.CreateCommand();

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

        private static Guid EventId(string value) => Guid.TryParse(value, out var id) ? id : Guid.Empty;

        private static string ReferrerHost(string? referrer) =>
            Uri.TryCreate(referrer, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;

        private static DateTime Utc(DateTime value) =>
            value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc);

        private static DateTime MinuteFloor(DateTime value) =>
            new(value.Ticks - value.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);

        private static long ToLong(object value) => value is DBNull ? 0 : Convert.ToInt64(value);
    }
}
