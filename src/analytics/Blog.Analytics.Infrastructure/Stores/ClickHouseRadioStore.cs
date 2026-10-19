using System.Data.Common;
using ClickHouse.Client.ADO;
using ClickHouse.Client.Copy;
using ClickHouse.Client.Utility;
using Microsoft.Extensions.Options;
using Blog.Analytics.Core.Radio;

namespace Blog.Analytics.Infrastructure.Stores
{
    public class ClickHouseRadioStore : IRadioStore
    {
        private const string PlayFields = "started_at, file, artist, album, title, is_request";

        private static readonly string[] PlayColumns = { "started_at", "file", "artist", "album", "title", "is_request" };
        private static readonly string[] ListenerColumns = { "sampled_at", "source", "listeners" };

        private readonly ClickHouseOptions _options;

        public ClickHouseRadioStore(IOptions<ClickHouseOptions> options)
        {
            _options = options.Value;
        }

        public bool IsConfigured => _options.Enabled;

        public async Task<RadioPlay?> LatestPlayAsync(CancellationToken cancellationToken = default) =>
            (await RecentPlaysAsync(1, cancellationToken)).FirstOrDefault();

        public Task WritePlayAsync(RadioPlay play, CancellationToken cancellationToken = default) =>
            InsertAsync(
                "radio_plays",
                PlayColumns,
                new object[] { Utc(play.StartedAt), play.File, play.Artist, play.Album, play.Title, play.IsRequest },
                cancellationToken);

        public Task<IReadOnlyList<RadioPlay>> RecentPlaysAsync(int limit, CancellationToken cancellationToken = default) =>
            QueryAsync(
                $"SELECT {PlayFields} FROM radio_plays ORDER BY started_at DESC LIMIT {{limit:UInt32}}",
                command => command.AddParameter("limit", (uint)Math.Max(limit, 1)),
                reader => new RadioPlay(
                    DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    Convert.ToBoolean(reader.GetValue(5))),
                cancellationToken);

        public Task WriteListenersAsync(ListenerSample sample, CancellationToken cancellationToken = default) =>
            InsertAsync(
                "radio_listeners",
                ListenerColumns,
                new object[] { Utc(sample.SampledAt), sample.Source, (uint)Math.Max(sample.Listeners, 0) },
                cancellationToken);

        public Task<IReadOnlyList<ListenerSample>> ListenersAsync(
            string source, DateTime from, DateTime to, CancellationToken cancellationToken = default) =>
            QueryAsync(
                """
                SELECT sampled_at, source, listeners
                FROM radio_listeners
                WHERE source = {source:String} AND sampled_at >= {from:DateTime} AND sampled_at < {to:DateTime}
                ORDER BY sampled_at
                """,
                command =>
                {
                    command.AddParameter("source", source);
                    command.AddParameter("from", Utc(from));
                    command.AddParameter("to", Utc(to));
                },
                reader => new ListenerSample(
                    DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc),
                    reader.GetString(1),
                    Convert.ToInt32(reader.GetValue(2))),
                cancellationToken);

        private async Task InsertAsync(string table, string[] columns, object[] row, CancellationToken cancellationToken)
        {
            using var connection = Connect();

            using var bulk = new ClickHouseBulkCopy(connection)
            {
                DestinationTableName = table,
                ColumnNames = columns,
                BatchSize = 1,
                MaxDegreeOfParallelism = 1
            };

            await bulk.InitAsync();
            await bulk.WriteToServerAsync(new[] { row }, cancellationToken);
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

        private static DateTime Utc(DateTime value) =>
            value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc);
    }
}
