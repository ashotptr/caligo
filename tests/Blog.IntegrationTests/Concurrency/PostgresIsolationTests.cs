using System.Data;
using Npgsql;
using Xunit;

namespace Blog.IntegrationTests.Concurrency;

// What each isolation level does and does not allow on PostgreSQL, one anomaly per test. Every
// interleaving is driven step by step on two connections. When a step must block, the test waits
// until pg_stat_activity shows that backend waiting on a lock, instead of sleeping and hoping.
[Collection(InfrastructureCollection.Name)]
public class PostgresIsolationTests : IAsyncLifetime
{
    private readonly InfrastructureFixture _infrastructure;
    private readonly string _schema = $"lab_{Guid.NewGuid():N}";

    public PostgresIsolationTests(InfrastructureFixture infrastructure)
    {
        _infrastructure = infrastructure;
    }

    public async Task InitializeAsync()
    {
        await using var connection = new NpgsqlConnection(_infrastructure.PostgresConnectionString);
        await connection.OpenAsync();

        await ExecAsync(connection, null, $"""
            CREATE SCHEMA {_schema};
            SET search_path TO {_schema};
            CREATE TABLE accounts (id int PRIMARY KEY, balance int NOT NULL);
            INSERT INTO accounts VALUES (1, 100), (2, 100);
            CREATE TABLE posts (id serial PRIMARY KEY, author text NOT NULL, status text NOT NULL);
            CREATE TABLE totals (post_id int PRIMARY KEY, claps int NOT NULL);
            CREATE TABLE pending (id serial PRIMARY KEY, post_id int NOT NULL, claps int NOT NULL);
            INSERT INTO totals VALUES (1, 60);
            INSERT INTO pending (post_id, claps) VALUES (1, 40);
            """);
    }

    public async Task DisposeAsync()
    {
        await using var connection = new NpgsqlConnection(_infrastructure.PostgresConnectionString);
        await connection.OpenAsync();

        await ExecAsync(connection, null, $"DROP SCHEMA {_schema} CASCADE");
    }

    [Fact]
    public async Task ThereAreNoDirtyReads_EvenAtReadUncommitted()
    {
        await using var writer = await OpenAsync();
        await using var reader = await OpenAsync();

        await using var write = await writer.BeginTransactionAsync();
        await ExecAsync(writer, write, "UPDATE accounts SET balance = 0 WHERE id = 1");

        // PostgreSQL runs READ UNCOMMITTED as READ COMMITTED.
        await using var read = await reader.BeginTransactionAsync(IsolationLevel.ReadUncommitted);

        Assert.Equal(100, await ScalarAsync<int>(reader, read, "SELECT balance FROM accounts WHERE id = 1"));
    }

    [Theory]
    [InlineData(IsolationLevel.ReadCommitted, 50)]
    [InlineData(IsolationLevel.RepeatableRead, 100)]
    public async Task ReReadingARow_AfterAnotherCommit(IsolationLevel level, int secondRead)
    {
        await using var reader = await OpenAsync();
        await using var writer = await OpenAsync();

        await using var read = await reader.BeginTransactionAsync(level);

        Assert.Equal(100, await ScalarAsync<int>(reader, read, "SELECT balance FROM accounts WHERE id = 1"));

        await ExecAsync(writer, null, "UPDATE accounts SET balance = 50 WHERE id = 1");

        Assert.Equal(secondRead, await ScalarAsync<int>(reader, read, "SELECT balance FROM accounts WHERE id = 1"));
    }

    // PostgreSQL's REPEATABLE READ is snapshot isolation, so it also rules out the phantoms the SQL
    // standard still permits at that level.
    [Theory]
    [InlineData(IsolationLevel.ReadCommitted, 1)]
    [InlineData(IsolationLevel.RepeatableRead, 0)]
    public async Task RecountingAPredicate_AfterAnotherTransactionInsertsAMatch(IsolationLevel level, long secondCount)
    {
        await using var reader = await OpenAsync();
        await using var writer = await OpenAsync();

        await using var read = await reader.BeginTransactionAsync(level);

        const string count = "SELECT count(*) FROM posts WHERE author = 'a' AND status = 'scheduled'";

        Assert.Equal(0, await ScalarAsync<long>(reader, read, count));

        await ExecAsync(writer, null, "INSERT INTO posts (author, status) VALUES ('a', 'scheduled')");

        Assert.Equal(secondCount, await ScalarAsync<long>(reader, read, count));
    }

    [Fact]
    public async Task ReadModifyWrite_AtReadCommitted_LosesAnUpdate()
    {
        await using var first = await OpenAsync();
        await using var second = await OpenAsync();

        await using var one = await first.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await using var two = await second.BeginTransactionAsync(IsolationLevel.ReadCommitted);

        var seenByOne = await ScalarAsync<int>(first, one, "SELECT balance FROM accounts WHERE id = 1");
        var seenByTwo = await ScalarAsync<int>(second, two, "SELECT balance FROM accounts WHERE id = 1");

        await ExecAsync(first, one, $"UPDATE accounts SET balance = {seenByOne - 10} WHERE id = 1");
        await one.CommitAsync();

        await ExecAsync(second, two, $"UPDATE accounts SET balance = {seenByTwo - 20} WHERE id = 1");
        await two.CommitAsync();

        // 100 - 10 - 20 should be 70; the first withdrawal vanished.
        Assert.Equal(80, await BalanceAsync());
    }

    [Fact]
    public async Task ReadModifyWrite_AtRepeatableRead_FailsTheLateWriterWith40001()
    {
        await using var first = await OpenAsync();
        await using var second = await OpenAsync();

        await using var one = await first.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        await using var two = await second.BeginTransactionAsync(IsolationLevel.RepeatableRead);

        await ScalarAsync<int>(first, one, "SELECT balance FROM accounts WHERE id = 1");
        var seenByTwo = await ScalarAsync<int>(second, two, "SELECT balance FROM accounts WHERE id = 1");

        await ExecAsync(first, one, "UPDATE accounts SET balance = 90 WHERE id = 1");
        await one.CommitAsync();

        var failure = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecAsync(second, two, $"UPDATE accounts SET balance = {seenByTwo - 20} WHERE id = 1"));

        Assert.Equal(PostgresErrorCodes.SerializationFailure, failure.SqlState);
        Assert.Equal(90, await BalanceAsync());
    }

    [Fact]
    public async Task AnAtomicUpdate_AtReadCommitted_LosesNothing()
    {
        await using var first = await OpenAsync();
        await using var second = await OpenAsync();

        await using var one = await first.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await using var two = await second.BeginTransactionAsync(IsolationLevel.ReadCommitted);

        await ExecAsync(first, one, "UPDATE accounts SET balance = balance - 10 WHERE id = 1");

        // Waits for the first transaction's row lock, then re-reads the committed row before applying.
        var blocked = ExecAsync(second, two, "UPDATE accounts SET balance = balance - 20 WHERE id = 1");

        await WaitUntilBlockedAsync(second.ProcessID);

        await one.CommitAsync();
        await blocked;
        await two.CommitAsync();

        Assert.Equal(70, await BalanceAsync());
    }

    [Fact]
    public async Task SelectForUpdate_TurnsReadModifyWriteIntoAQueue()
    {
        await using var first = await OpenAsync();
        await using var second = await OpenAsync();

        await using var one = await first.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await using var two = await second.BeginTransactionAsync(IsolationLevel.ReadCommitted);

        var seenByOne = await ScalarAsync<int>(first, one, "SELECT balance FROM accounts WHERE id = 1 FOR UPDATE");

        var secondRead = ScalarAsync<int>(second, two, "SELECT balance FROM accounts WHERE id = 1 FOR UPDATE");

        await WaitUntilBlockedAsync(second.ProcessID);

        await ExecAsync(first, one, $"UPDATE accounts SET balance = {seenByOne - 10} WHERE id = 1");
        await one.CommitAsync();

        var seenByTwo = await secondRead;

        await ExecAsync(second, two, $"UPDATE accounts SET balance = {seenByTwo - 20} WHERE id = 1");
        await two.CommitAsync();

        Assert.Equal(90, seenByTwo);
        Assert.Equal(70, await BalanceAsync());
    }

    // The scheduling quota: count an author's scheduled posts, insert only if under the limit. Two
    // such transactions read the same count and write rows the other's count should have included.
    [Fact]
    public async Task CountThenInsert_AtRepeatableRead_OverfillsTheQuota()
    {
        var (first, second) = await RaceToScheduleAsync(IsolationLevel.RepeatableRead);

        Assert.Null(first);
        Assert.Null(second);
        Assert.Equal(2, await ScheduledCountAsync());
    }

    [Fact]
    public async Task CountThenInsert_AtSerializable_AbortsOneOfTheTwoWith40001()
    {
        var (first, second) = await RaceToScheduleAsync(IsolationLevel.Serializable);

        var failures = new[] { first, second }.Where(f => f is not null).ToList();

        Assert.Equal(PostgresErrorCodes.SerializationFailure, Assert.Single(failures)!.SqlState);
        Assert.Equal(1, await ScheduledCountAsync());
    }

    // The clap audit reads totals, then pending. A rollup moving 40 claps from pending to totals
    // between the two reads makes a READ COMMITTED audit see 60 + 0; one snapshot sees 100 either way.
    [Theory]
    [InlineData(IsolationLevel.ReadCommitted, 60)]
    [InlineData(IsolationLevel.RepeatableRead, 100)]
    public async Task AMultiQueryAudit_RacingARollup(IsolationLevel level, int audited)
    {
        await using var auditor = await OpenAsync();
        await using var rollup = await OpenAsync();

        await using var audit = await auditor.BeginTransactionAsync(level);

        var total = await ScalarAsync<int>(auditor, audit, "SELECT claps FROM totals WHERE post_id = 1");

        await ExecAsync(rollup, null, """
            WITH moved AS (DELETE FROM pending WHERE post_id = 1 RETURNING claps)
            UPDATE totals SET claps = claps + (SELECT sum(claps) FROM moved) WHERE post_id = 1
            """);

        var queued = await ScalarAsync<long>(
            auditor, audit, "SELECT coalesce(sum(claps), 0) FROM pending WHERE post_id = 1");

        Assert.Equal(audited, total + queued);
    }

    [Fact]
    public async Task UpdatingTwoRowsInOppositeOrders_Deadlocks_AndPostgresAbortsOneSideWith40P01()
    {
        await using var first = await OpenAsync();
        await using var second = await OpenAsync();

        await using var one = await first.BeginTransactionAsync();
        await using var two = await second.BeginTransactionAsync();

        await ExecAsync(first, one, "UPDATE accounts SET balance = balance - 1 WHERE id = 1");
        await ExecAsync(second, two, "UPDATE accounts SET balance = balance - 1 WHERE id = 2");

        var oneWantsTwo = CaptureAsync(() => ExecAsync(first, one, "UPDATE accounts SET balance = balance + 1 WHERE id = 2"));

        await WaitUntilBlockedAsync(first.ProcessID);

        var twoWantsOne = CaptureAsync(() => ExecAsync(second, two, "UPDATE accounts SET balance = balance + 1 WHERE id = 1"));

        var outcomes = await Task.WhenAll(oneWantsTwo, twoWantsOne);

        Assert.Equal(PostgresErrorCodes.DeadlockDetected, Assert.Single(outcomes, o => o is not null)!.SqlState);
    }

    [Fact]
    public async Task UpdatingRowsInTheSameOrder_OnlyQueues_AndBothTransactionsCommit()
    {
        await using var first = await OpenAsync();
        await using var second = await OpenAsync();

        await using var one = await first.BeginTransactionAsync();
        await using var two = await second.BeginTransactionAsync();

        await ExecAsync(first, one, "UPDATE accounts SET balance = balance - 1 WHERE id = 1");

        var queued = ExecAsync(second, two, "UPDATE accounts SET balance = balance - 1 WHERE id = 1");

        await WaitUntilBlockedAsync(second.ProcessID);

        await ExecAsync(first, one, "UPDATE accounts SET balance = balance - 1 WHERE id = 2");
        await one.CommitAsync();

        await queued;
        await ExecAsync(second, two, "UPDATE accounts SET balance = balance - 1 WHERE id = 2");
        await two.CommitAsync();

        Assert.Equal(98, await BalanceAsync());
    }

    private async Task<(PostgresException? First, PostgresException? Second)> RaceToScheduleAsync(IsolationLevel level)
    {
        const int quota = 1;

        await using var first = await OpenAsync();
        await using var second = await OpenAsync();

        await using var one = await first.BeginTransactionAsync(level);
        await using var two = await second.BeginTransactionAsync(level);

        const string count = "SELECT count(*) FROM posts WHERE author = 'a' AND status = 'scheduled'";
        const string insert = "INSERT INTO posts (author, status) VALUES ('a', 'scheduled')";

        Assert.True(await ScalarAsync<long>(first, one, count) < quota);
        Assert.True(await ScalarAsync<long>(second, two, count) < quota);

        var firstOutcome = await CaptureAsync(async () =>
        {
            await ExecAsync(first, one, insert);
            await one.CommitAsync();
        });

        var secondOutcome = await CaptureAsync(async () =>
        {
            await ExecAsync(second, two, insert);
            await two.CommitAsync();
        });

        return (firstOutcome, secondOutcome);
    }

    private static async Task<PostgresException?> CaptureAsync(Func<Task> step)
    {
        try
        {
            await step();

            return null;
        }
        catch (PostgresException ex)
        {
            return ex;
        }
    }

    private async Task<int> BalanceAsync()
    {
        await using var connection = await OpenAsync();

        return await ScalarAsync<int>(connection, null, "SELECT balance FROM accounts WHERE id = 1");
    }

    private async Task<long> ScheduledCountAsync()
    {
        await using var connection = await OpenAsync();

        return await ScalarAsync<long>(connection, null, "SELECT count(*) FROM posts WHERE status = 'scheduled'");
    }

    private async Task WaitUntilBlockedAsync(int processId)
    {
        await using var observer = await OpenAsync();

        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (await ScalarAsync<long>(
                   observer, null,
                   $"SELECT count(*) FROM pg_stat_activity WHERE pid = {processId} AND wait_event_type = 'Lock'") == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, $"backend {processId} never started waiting on a lock");

            await Task.Delay(20);
        }
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(_infrastructure.PostgresConnectionString);

        await connection.OpenAsync();

        await ExecAsync(connection, null, $"SET search_path TO {_schema}");

        return connection;
    }

    private static async Task<int> ExecAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);

        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);

        return (T)Convert.ChangeType((await command.ExecuteScalarAsync())!, typeof(T));
    }
}
