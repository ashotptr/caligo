using System.Data;
using NBomber.CSharp;
using Npgsql;

namespace Blog.LoadTests.Scenarios;

public static class HotRow
{
    private const int Shards = 16;

    private sealed record Strategy(string Name, Func<NpgsqlDataSource, Counters, Task> Increment, bool FoldsInBackground = false);

    private sealed class Counters
    {
        public long Retries;
    }

    private static readonly Strategy[] Strategies =
    {
        new("naive read-modify-write", async (source, _) =>
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);

            var value = await ScalarAsync(connection, transaction, "SELECT value FROM counters WHERE id = 1");

            await ExecAsync(connection, transaction, "UPDATE counters SET value = @v WHERE id = 1", value + 1);
            await transaction.CommitAsync();
        }),

        new("atomic UPDATE", async (source, _) =>
        {
            await using var command = source.CreateCommand("UPDATE counters SET value = value + 1 WHERE id = 1");

            await command.ExecuteNonQueryAsync();
        }),

        new("SELECT ... FOR UPDATE", async (source, _) =>
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);

            var value = await ScalarAsync(connection, transaction, "SELECT value FROM counters WHERE id = 1 FOR UPDATE");

            await ExecAsync(connection, transaction, "UPDATE counters SET value = @v WHERE id = 1", value + 1);
            await transaction.CommitAsync();
        }),

        new("optimistic version + retry", async (source, counters) =>
        {
            while (true)
            {
                await using var connection = await source.OpenConnectionAsync();

                await using var read = new NpgsqlCommand("SELECT value, version FROM counters WHERE id = 1", connection);
                await using var reader = await read.ExecuteReaderAsync();

                await reader.ReadAsync();

                var (value, version) = (reader.GetInt64(0), reader.GetInt32(1));

                await reader.DisposeAsync();

                await using var write = new NpgsqlCommand(
                    "UPDATE counters SET value = @v, version = version + 1 WHERE id = 1 AND version = @version", connection);

                write.Parameters.AddWithValue("v", value + 1);
                write.Parameters.AddWithValue("version", version);

                if (await write.ExecuteNonQueryAsync() == 1)
                {
                    return;
                }

                Interlocked.Increment(ref counters.Retries);
            }
        }),

        new("SERIALIZABLE + retry", async (source, counters) =>
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await using var connection = await source.OpenConnectionAsync();
                    await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable);

                    var value = await ScalarAsync(connection, transaction, "SELECT value FROM counters WHERE id = 1");

                    await ExecAsync(connection, transaction, "UPDATE counters SET value = @v WHERE id = 1", value + 1);
                    await transaction.CommitAsync();

                    return;
                }
                catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.SerializationFailure)
                {
                    Interlocked.Increment(ref counters.Retries);

                    await Task.Delay(Random.Shared.Next(1, Math.Min(50, 2 << Math.Min(attempt, 5))));
                }
            }
        }),

        new($"sharded counter ({Shards} rows)", async (source, _) =>
        {
            await using var command = source.CreateCommand(
                "UPDATE counter_shards SET value = value + 1 WHERE id = 1 AND shard = @shard");

            command.Parameters.AddWithValue("shard", Random.Shared.Next(Shards));

            await command.ExecuteNonQueryAsync();
        }),

        new("append + batch rollup", async (source, _) =>
        {
            await using var command = source.CreateCommand("INSERT INTO counter_deltas (delta) VALUES (1)");

            await command.ExecuteNonQueryAsync();
        }, FoldsInBackground: true)
    };

    public static async Task<Summary> RunAsync(LoadTestSettings settings, CommandOptions options)
    {
        var copies = options.Int("copies", 64);
        var seconds = options.Int("seconds", 15);

        var summary = new Summary("hot-row");

        await EnsureDatabaseAsync(settings.LabDb);

        await using var source = NpgsqlDataSource.Create(settings.LabDb);

        await WarmPoolAsync(source, copies);

        summary.Line($"| concurrent writers | {copies} |");
        summary.Line($"| duration per strategy | {seconds} s |");
        summary.Line(string.Empty);
        summary.Line("| strategy | increments/s | p50 ms | p99 ms | retries | final / succeeded | correct |");
        summary.Line("|---|---|---|---|---|---|---|");

        foreach (var strategy in Strategies)
        {
            await ResetAsync(source);

            var counters = new Counters();

            using var folding = new CancellationTokenSource();

            var folder = strategy.FoldsInBackground
                ? Task.Run(() => FoldUntilCancelledAsync(source, folding.Token))
                : Task.CompletedTask;

            var scenario = Scenario.Create(Slug(strategy.Name), async _ =>
            {
                try
                {
                    await strategy.Increment(source, counters);

                    return Response.Ok();
                }
                catch (Exception ex)
                {
                    return Response.Fail(message: ex.Message);
                }
            })
            .WithoutWarmUp()
            .WithLoadSimulations(Simulation.KeepConstant(copies: copies, during: TimeSpan.FromSeconds(seconds)));

            var stats = Runner.Run(settings, scenario);

            folding.Cancel();
            await folder;

            while (await FoldAsync(source) > 0)
            {
            }

            var final = await CounterValueAsync(source);
            var succeeded = stats.Ok.Request.Count;

            summary.Line(
                $"| {strategy.Name} | {stats.Ok.Request.RPS:0} | {stats.Ok.Latency.Percent50:0.00} | " +
                $"{stats.Ok.Latency.Percent99:0.00} | {counters.Retries} | {final} / {succeeded} | " +
                $"{(final == succeeded ? "yes" : $"no, {succeeded - final} lost")} |");

            if (strategy.Name.StartsWith("naive", StringComparison.Ordinal))
            {
                summary.Check("the naive version loses updates, as it should", final < succeeded, $"{succeeded - final} lost");
            }
            else
            {
                summary.Check($"{strategy.Name} loses nothing", final == succeeded, $"{final} / {succeeded}");
            }
        }

        return summary;
    }

    private static async Task WarmPoolAsync(NpgsqlDataSource source, int connections)
    {
        var opened = await Task.WhenAll(Enumerable.Range(0, connections).Select(_ => source.OpenConnectionAsync().AsTask()));

        foreach (var connection in opened)
        {
            await connection.DisposeAsync();
        }
    }

    private static async Task FoldUntilCancelledAsync(NpgsqlDataSource source, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (await FoldAsync(source) < 5000)
            {
                try
                {
                    await Task.Delay(100, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
    }

    private static async Task<long> FoldAsync(NpgsqlDataSource source)
    {
        await using var command = source.CreateCommand("""
            WITH batch AS (
                DELETE FROM counter_deltas
                WHERE id IN (SELECT id FROM counter_deltas ORDER BY id LIMIT 5000 FOR UPDATE SKIP LOCKED)
                RETURNING delta
            ), folded AS (
                UPDATE counters SET value = value + (SELECT coalesce(sum(delta), 0) FROM batch) WHERE id = 1
            )
            SELECT count(*) FROM batch
            """);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> CounterValueAsync(NpgsqlDataSource source)
    {
        await using var command = source.CreateCommand("""
            SELECT (SELECT value FROM counters WHERE id = 1)
                 + (SELECT coalesce(sum(value), 0) FROM counter_shards WHERE id = 1)
            """);

        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task ResetAsync(NpgsqlDataSource source)
    {
        await using var command = source.CreateCommand($"""
            CREATE TABLE IF NOT EXISTS counters (id int PRIMARY KEY, value bigint NOT NULL, version int NOT NULL);
            CREATE TABLE IF NOT EXISTS counter_shards (id int, shard int, value bigint NOT NULL, PRIMARY KEY (id, shard));
            CREATE TABLE IF NOT EXISTS counter_deltas (id bigserial PRIMARY KEY, delta int NOT NULL);
            TRUNCATE counters, counter_shards, counter_deltas;
            INSERT INTO counters VALUES (1, 0, 0);
            INSERT INTO counter_shards SELECT 1, shard, 0 FROM generate_series(0, {Shards - 1}) AS shard;
            """);

        await command.ExecuteNonQueryAsync();
    }

    private static async Task EnsureDatabaseAsync(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var database = builder.Database!;

        builder.Database = "postgres";

        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();

        await using var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", connection);

        exists.Parameters.AddWithValue("name", database);

        if (await exists.ExecuteScalarAsync() is null)
        {
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", connection);

            await create.ExecuteNonQueryAsync();
        }
    }

    private static async Task<long> ScalarAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, long value)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);

        command.Parameters.AddWithValue("v", value);

        await command.ExecuteNonQueryAsync();
    }

    private static string Slug(string name) =>
        new string(name.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_').ToArray()).Trim('_');
}
