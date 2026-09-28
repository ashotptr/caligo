using ClickHouse.Client.ADO;
using Confluent.Kafka;
using Npgsql;

namespace Blog.LoadTests;

public static class Db
{
    public static async Task<long> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 300 };

        var value = await command.ExecuteScalarAsync();

        return value is null or DBNull ? 0 : Convert.ToInt64(value);
    }

    public static async Task ExecAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 300 };

        await command.ExecuteNonQueryAsync();
    }

    public static async Task<long> ClickHouseScalarAsync(string connectionString, string sql)
    {
        using var connection = new ClickHouseConnection(connectionString);
        using var command = connection.CreateCommand();

        command.CommandText = sql;

        var value = await command.ExecuteScalarAsync();

        return value is null or DBNull ? 0 : Convert.ToInt64(value);
    }

    // How many times statements matching the pattern have run, per pg_stat_statements. Null when
    // the extension is not loaded (the load-test compose override loads it).
    public static async Task<long?> StatementCallsAsync(string connectionString, string likePattern)
    {
        try
        {
            await ExecAsync(connectionString, "CREATE EXTENSION IF NOT EXISTS pg_stat_statements");

            return await ScalarAsync(
                connectionString,
                $"SELECT coalesce(sum(calls), 0) FROM pg_stat_statements WHERE query LIKE '{likePattern.Replace("'", "''")}'");
        }
        catch (PostgresException)
        {
            return null;
        }
    }

    public static async Task<IReadOnlyList<string>> TopStatementsAsync(string connectionString, int limit)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            await using var command = new NpgsqlCommand($"""
                SELECT calls, round(total_exec_time::numeric, 1), round(mean_exec_time::numeric, 3),
                       regexp_replace(left(query, 110), '\s+', ' ', 'g')
                FROM pg_stat_statements
                WHERE dbid = (SELECT oid FROM pg_database WHERE datname = current_database())
                ORDER BY total_exec_time DESC
                LIMIT {limit}
                """, connection);

            await using var reader = await command.ExecuteReaderAsync();

            var rows = new List<string>();

            while (await reader.ReadAsync())
            {
                rows.Add($"| {reader.GetInt64(0)} | {reader.GetDecimal(1)} | {reader.GetDecimal(2)} | `{reader.GetString(3)}` |");
            }

            return rows;
        }
        catch (PostgresException)
        {
            return Array.Empty<string>();
        }
    }

    public static async Task ResetStatementsAsync(string connectionString)
    {
        try
        {
            await ExecAsync(connectionString, "CREATE EXTENSION IF NOT EXISTS pg_stat_statements; SELECT pg_stat_statements_reset();");
        }
        catch (PostgresException)
        {
        }
    }

    // For polling loops: null when the probe itself failed, so the loop asks again instead of the
    // whole run dying because a database was too busy to answer one of its questions in time.
    public static async Task<long?> TryScalarAsync(string connectionString, string sql)
    {
        try
        {
            return await ScalarAsync(connectionString, sql);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            Console.WriteLine($"Probe failed, asking again: {ex.Message}");

            return null;
        }
    }

    // Polls until the condition holds; returns how long that took, or null on timeout. A probe
    // that fails counts as "not yet".
    public static async Task<TimeSpan?> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var started = DateTime.UtcNow;

        while (DateTime.UtcNow - started < timeout)
        {
            try
            {
                if (await condition())
                {
                    return DateTime.UtcNow - started;
                }
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                Console.WriteLine($"Probe failed, asking again: {ex.Message}");
            }

            await Task.Delay(200);
        }

        return null;
    }

    // Connection and timeout failures, not errors the server reported about the statement itself.
    // The probes pass no cancellation token, so a TaskCanceledException is an HttpClient timeout.
    private static bool IsTransient(Exception ex) =>
        ex is TimeoutException or TaskCanceledException or HttpRequestException or IOException or KafkaException
        || (ex is NpgsqlException && ex is not PostgresException);
}
