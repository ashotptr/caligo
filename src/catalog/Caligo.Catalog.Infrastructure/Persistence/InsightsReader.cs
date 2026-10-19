using System.Collections;
using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Caligo.Catalog.Application.Insights;

namespace Caligo.Catalog.Infrastructure.Persistence
{
    public class InsightsReader : IInsights
    {
        public const int MaxRows = 500;
        public const string QueriesResource = "StreamingQueries.sql";

        private static readonly Lazy<IReadOnlyList<SavedQuery>> Saved = new(LoadQueries);

        private readonly StreamingDbContext _db;

        public InsightsReader(StreamingDbContext db)
        {
            _db = db;
        }

        public IReadOnlyList<SavedQueryDto> Queries =>
            Saved.Value.Select(q => new SavedQueryDto(q.Number, q.Title)).ToList();

        public async Task<IReadOnlyList<PopularContentDto>> GetPopularAsync(CancellationToken cancellationToken = default)
        {
            var rows = await _db.PopularContent.AsNoTracking().ToListAsync(cancellationToken);

            return rows
                .OrderByDescending(r => r.TotalViews)
                .ThenBy(r => r.ContentId)
                .Select(r => new PopularContentDto(r.ContentId, r.Title, r.TotalViews))
                .ToList();
        }

        public async Task<IReadOnlyList<EngagingContentDto>> GetEngagingAsync(CancellationToken cancellationToken = default)
        {
            var rows = await _db.EngagingContent.AsNoTracking().ToListAsync(cancellationToken);

            return rows
                .OrderByDescending(r => r.EngagementRatio.HasValue)
                .ThenByDescending(r => r.EngagementRatio)
                .ThenBy(r => r.ContentId)
                .Select(r => new EngagingContentDto(
                    r.ContentId, r.Title, r.TotalViews, r.TotalInteractions, r.CommentsLeft, r.EngagementRatio))
                .ToList();
        }

        public async Task<IReadOnlyList<ActiveUserDto>> GetActiveUsersAsync(CancellationToken cancellationToken = default)
        {
            var rows = await _db.ActiveUsers.AsNoTracking().ToListAsync(cancellationToken);

            return rows
                .OrderByDescending(r => r.TotalViews + r.TotalInteractions + r.CommentsLeft)
                .ThenBy(r => r.UserId)
                .Select(r => new ActiveUserDto(r.UserId, r.FullName, r.TotalViews, r.TotalInteractions, r.CommentsLeft))
                .ToList();
        }

        public async Task<QueryResultDto?> RunQueryAsync(int number, CancellationToken cancellationToken = default)
        {
            var query = Saved.Value.FirstOrDefault(q => q.Number == number);

            if (query is null)
            {
                return null;
            }

            var connection = _db.Database.GetDbConnection();
            var opened = connection.State != ConnectionState.Open;

            if (opened)
            {
                await connection.OpenAsync(cancellationToken);
            }

            try
            {
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

                await ExecuteAsync(connection, transaction, "SET TRANSACTION READ ONLY", cancellationToken);
                await ExecuteAsync(connection, transaction, "SET LOCAL search_path TO streamingservice", cancellationToken);

                await using var command = connection.CreateCommand();

                command.Transaction = transaction;
                command.CommandText = query.Sql;

                var columns = new List<string>();
                var rows = new List<List<object?>>();

                await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                {
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        columns.Add(reader.GetName(i));
                    }

                    while (rows.Count < MaxRows && await reader.ReadAsync(cancellationToken))
                    {
                        var row = new List<object?>(reader.FieldCount);

                        for (var i = 0; i < reader.FieldCount; i++)
                        {
                            row.Add(Present(reader.IsDBNull(i) ? null : reader.GetValue(i), reader.GetDataTypeName(i)));
                        }

                        rows.Add(row);
                    }
                }

                await transaction.RollbackAsync(cancellationToken);

                return new QueryResultDto(query.Number, query.Title, columns, rows);
            }
            finally
            {
                if (opened)
                {
                    await connection.CloseAsync();
                }
            }
        }

        public static IReadOnlyList<SavedQuery> Parse(string text)
        {
            var queries = new List<SavedQuery>();
            var blocks = Regex.Split(text.Replace("\r\n", "\n"), @"^-- Query (?=\d+)", RegexOptions.Multiline);

            foreach (var block in blocks.Skip(1))
            {
                var lines = block.Split('\n');

                if (!int.TryParse(lines[0].Trim(), out var number))
                {
                    continue;
                }

                var title = lines.Skip(1).FirstOrDefault(l => l.StartsWith("-- ", StringComparison.Ordinal))?[3..].Trim()
                            ?? $"Query {number}";

                var sql = string.Join('\n', lines.Skip(1).Where(l => !l.StartsWith("--", StringComparison.Ordinal)))
                    .Trim()
                    .TrimEnd(';')
                    .Trim();

                if (sql.Length > 0)
                {
                    queries.Add(new SavedQuery(number, title, sql));
                }
            }

            return queries;
        }

        private static IReadOnlyList<SavedQuery> LoadQueries()
        {
            using var stream = typeof(InsightsReader).Assembly.GetManifestResourceStream(QueriesResource)
                               ?? throw new InvalidOperationException($"Embedded resource {QueriesResource} is missing.");

            using var reader = new StreamReader(stream);

            return Parse(reader.ReadToEnd());
        }

        private static async Task ExecuteAsync(
            DbConnection connection, DbTransaction transaction, string sql, CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand();

            command.Transaction = transaction;
            command.CommandText = sql;

            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static object? Present(object? value, string typeName) => value switch
        {
            null => null,
            DateTime date when typeName == "date" => date.ToString("yyyy-MM-dd"),
            DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss"),
            DateOnly date => date.ToString("yyyy-MM-dd"),
            BitArray bits => bits.Count > 0 && bits[0] ? 1 : 0,
            bool flag when typeName.StartsWith("bit", StringComparison.Ordinal) => flag ? 1 : 0,
            float number => Math.Round((double)number, 4),
            _ => value
        };

        public sealed record SavedQuery(int Number, string Title, string Sql);
    }
}
