using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using Blog.Notifications.Host.Persistence;
using Blog.Posts.Application.Posts;
using Blog.Posts.Infrastructure.Persistence;
using Xunit;

namespace Blog.IntegrationTests.Concurrency;

[Collection(InfrastructureCollection.Name)]
public class QueryPlanTests : IAsyncLifetime
{
    private const int Authors = 100;
    private const int PublishedPosts = 60_000;
    private const int ScheduledPosts = 500;

    private static readonly Regex RowComparison = new(@"\(\w+\.""PublishedDate"", \w+\.""Id""\) <");

    private static readonly SemaphoreSlim SeedLock = new(1, 1);
    private static bool _seeded;

    private readonly InfrastructureFixture _infrastructure;

    public QueryPlanTests(InfrastructureFixture infrastructure)
    {
        _infrastructure = infrastructure;
    }

    private string PostsDatabase => WithDatabase(_infrastructure.PostgresConnectionString, "blogplans");

    private string NotificationsDatabase => WithDatabase(_infrastructure.PostgresConnectionString, "blognotificationsplans");

    public async Task InitializeAsync()
    {
        await SeedLock.WaitAsync();

        try
        {
            if (!_seeded)
            {
                await SeedPostsAsync();
                await SeedSubscribersAsync();

                _seeded = true;
            }
        }
        finally
        {
            SeedLock.Release();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ThePublicFeed_IsReadFromThePartialPublishedIndex()
    {
        var plan = await ExplainAsync(PostsDatabase, """
            SELECT "Id", "Title" FROM "BlogPosts"
            WHERE "Status" = 0
            ORDER BY "PublishedDate" DESC, "Id" DESC
            LIMIT 21
            """);

        Assert.Contains("IX_BlogPosts_Published_PublishedDate_Id", IndexesUsed(plan));
    }

    [Fact]
    public async Task AContinuationPageDeepInTheFeed_ReadsOnlyThatPage_WhenTheCursorIsARowComparison()
    {
        var (date, id) = await CursorAtAsync(40_000);

        var plan = await ExplainAsync(PostsDatabase, $"""
            SELECT "Id", "Title" FROM "BlogPosts"
            WHERE "Status" = 0 AND ("PublishedDate", "Id") < ('{Literal(date)}', {id})
            ORDER BY "PublishedDate" DESC, "Id" DESC
            LIMIT 21
            """, analyze: true);

        Assert.Contains("IX_BlogPosts_Published_PublishedDate_Id", IndexesUsed(plan));
        Assert.True(RowsScanned(plan) <= 21, $"{RowsScanned(plan)} rows were scanned for a 21-row page");
    }

    [Fact]
    public async Task TheSameCursorWrittenAsAnOr_ReadsEveryNewerPostFirst()
    {
        var (date, id) = await CursorAtAsync(40_000);

        var plan = await ExplainAsync(PostsDatabase, $"""
            SELECT "Id", "Title" FROM "BlogPosts"
            WHERE "Status" = 0
              AND ("PublishedDate" < '{Literal(date)}' OR ("PublishedDate" = '{Literal(date)}' AND "Id" < {id}))
            ORDER BY "PublishedDate" DESC, "Id" DESC
            LIMIT 21
            """, analyze: true);

        Assert.True(RowsScanned(plan) > 10_000, $"only {RowsScanned(plan)} rows were scanned");
    }

    [Fact]
    public async Task TheRepositoryPagesWithTheRowComparison()
    {
        var executed = new List<string>();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(PostsDatabase)
            .LogTo(executed.Add, new[] { RelationalEventId.CommandExecuted })
            .Options;

        await using var context = new ApplicationDbContext(options);

        var repository = new EfPostRepository(
            context, new LoggingDomainEventDispatcher(NullLogger<LoggingDomainEventDispatcher>.Instance));

        var (date, id) = await CursorAtAsync(40_000);

        var page = await repository.GetPageAsync(new PostCursor(date, id), 20, authorId: null);

        Assert.Equal(20, page.Items.Count);
        Assert.Contains(executed, sql => RowComparison.IsMatch(sql));
    }

    [Fact]
    public async Task MyPosts_AreReadFromTheAuthorIndex()
    {
        var plan = await ExplainAsync(PostsDatabase, """
            SELECT "Id", "Title" FROM "BlogPosts"
            WHERE "AuthorId" = 'author-042'
            ORDER BY "PublishedDate" DESC, "Id" DESC
            LIMIT 21
            """);

        Assert.Contains("IX_BlogPosts_Author_PublishedDate_Id", IndexesUsed(plan));
    }

    [Fact]
    public async Task TheSchedulerClaim_UsesThePartialDueIndex()
    {
        var plan = await ExplainAsync(PostsDatabase, """
            SELECT * FROM "BlogPosts"
            WHERE "Status" = 1 AND "ScheduledFor" <= now() + interval '10 days'
            ORDER BY "ScheduledFor", "Id"
            LIMIT 100
            FOR UPDATE SKIP LOCKED
            """);

        Assert.Contains("IX_BlogPosts_Scheduled_Due", IndexesUsed(plan));
    }

    [Fact]
    public async Task AReadersClapCount_IsASinglePrimaryKeyLookup()
    {
        var plan = await ExplainAsync(PostsDatabase, """
            SELECT "Claps" FROM post_claps WHERE "PostId" = 1234 AND "UserId" = 'reader-7'
            """);

        Assert.Contains("PK_post_claps", IndexesUsed(plan));
    }

    [Fact]
    public async Task AFanOutChunk_WalksOnlyActiveSubscribersInIdOrder()
    {
        var plan = await ExplainAsync(NotificationsDatabase, """
            SELECT * FROM subscribers
            WHERE "IsActive" AND "Id" > '00000000-0000-0000-0000-000000000000'
            ORDER BY "Id"
            LIMIT 5000
            """);

        Assert.Contains("IX_subscribers_active_id", IndexesUsed(plan));
        Assert.DoesNotContain("Sort", NodeTypes(plan));
    }

    private async Task<(DateTime Date, int Id)> CursorAtAsync(int offset)
    {
        await using var connection = new NpgsqlConnection(PostsDatabase);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand($"""
            SELECT "PublishedDate", "Id" FROM "BlogPosts"
            WHERE "Status" = 0
            ORDER BY "PublishedDate" DESC, "Id" DESC
            OFFSET {offset} LIMIT 1
            """, connection);

        await using var reader = await command.ExecuteReaderAsync();

        await reader.ReadAsync();

        return (reader.GetDateTime(0), reader.GetInt32(1));
    }

    private async Task SeedPostsAsync()
    {
        await using (var context = new ApplicationDbContext(
                         new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(PostsDatabase).Options))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.MigrateAsync();
        }

        await using var connection = new NpgsqlConnection(PostsDatabase);
        await connection.OpenAsync();

        await using (var users = await connection.BeginBinaryImportAsync("""
            COPY "AspNetUsers" ("Id", "UserName", "EmailConfirmed", "PhoneNumberConfirmed",
                                "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
            FROM STDIN (FORMAT BINARY)
            """))
        {
            for (var i = 0; i < Authors; i++)
            {
                await users.StartRowAsync();
                await users.WriteAsync($"author-{i:D3}", NpgsqlDbType.Text);
                await users.WriteAsync($"author-{i:D3}", NpgsqlDbType.Varchar);
                await users.WriteAsync(false, NpgsqlDbType.Boolean);
                await users.WriteAsync(false, NpgsqlDbType.Boolean);
                await users.WriteAsync(false, NpgsqlDbType.Boolean);
                await users.WriteAsync(false, NpgsqlDbType.Boolean);
                await users.WriteAsync(0, NpgsqlDbType.Integer);
            }

            await users.CompleteAsync();
        }

        var start = DateTime.UtcNow.AddDays(-400);

        await using (var posts = await connection.BeginBinaryImportAsync("""
            COPY "BlogPosts" ("Title", "Content", "PublishedDate", "AuthorId", "Tags", "Status", "ScheduledFor", "Version")
            FROM STDIN (FORMAT BINARY)
            """))
        {
            for (var i = 0; i < PublishedPosts + ScheduledPosts; i++)
            {
                var scheduled = i >= PublishedPosts;
                var published = scheduled
                    ? DateTime.UtcNow.AddDays(1 + i % 7)
                    : start.AddMinutes(i * 9);

                await posts.StartRowAsync();
                await posts.WriteAsync($"Post {i}", NpgsqlDbType.Varchar);
                await posts.WriteAsync("A short body.", NpgsqlDbType.Text);
                await posts.WriteAsync(published, NpgsqlDbType.TimestampTz);
                await posts.WriteAsync($"author-{i % Authors:D3}", NpgsqlDbType.Text);
                await posts.WriteAsync(Array.Empty<string>(), NpgsqlDbType.Array | NpgsqlDbType.Text);
                await posts.WriteAsync(scheduled ? 1 : 0, NpgsqlDbType.Integer);

                if (scheduled)
                {
                    await posts.WriteAsync(published, NpgsqlDbType.TimestampTz);
                }
                else
                {
                    await posts.WriteNullAsync();
                }

                await posts.WriteAsync(0, NpgsqlDbType.Integer);
            }

            await posts.CompleteAsync();
        }

        await ExecAsync(connection, """
            INSERT INTO post_claps ("PostId", "UserId", "Claps", "LastApplied", "UpdatedAt")
            SELECT p."Id", 'reader-' || r, 1 + (p."Id" + r) % 50, 1, now()
            FROM "BlogPosts" p CROSS JOIN generate_series(1, 3) AS r
            WHERE p."Status" = 0 AND p."Id" % 5 = 0;

            ANALYZE;
            """);
    }

    private async Task SeedSubscribersAsync()
    {
        await using (var context = new NotificationsDbContext(
                         new DbContextOptionsBuilder<NotificationsDbContext>().UseNpgsql(NotificationsDatabase).Options))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.MigrateAsync();
        }

        await using var connection = new NpgsqlConnection(NotificationsDatabase);
        await connection.OpenAsync();

        await using (var writer = await connection.BeginBinaryImportAsync(
                         """COPY subscribers ("Id", "Email", "IsActive", "SubscribedAt", "UnsubscribeToken") FROM STDIN (FORMAT BINARY)"""))
        {
            for (var i = 0; i < 40_000; i++)
            {
                var id = Guid.NewGuid();

                await writer.StartRowAsync();
                await writer.WriteAsync(id, NpgsqlDbType.Uuid);
                await writer.WriteAsync($"{id:N}@example.test", NpgsqlDbType.Varchar);
                await writer.WriteAsync(i % 2 == 0, NpgsqlDbType.Boolean);
                await writer.WriteAsync(DateTime.UtcNow, NpgsqlDbType.TimestampTz);
                await writer.WriteAsync(id.ToString("N"), NpgsqlDbType.Varchar);
            }

            await writer.CompleteAsync();
        }

        await ExecAsync(connection, "ANALYZE");
    }

    private static async Task<JsonElement> ExplainAsync(string connectionString, string sql, bool analyze = false)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            $"EXPLAIN ({(analyze ? "ANALYZE, " : string.Empty)}FORMAT JSON) {sql}", connection);

        var json = (string)(await command.ExecuteScalarAsync())!;

        return JsonDocument.Parse(json).RootElement[0].GetProperty("Plan");
    }

    private static IReadOnlyList<string> IndexesUsed(JsonElement plan) =>
        Nodes(plan)
            .Where(node => node.TryGetProperty("Index Name", out _))
            .Select(node => node.GetProperty("Index Name").GetString()!)
            .ToList();

    private static IReadOnlyList<string> NodeTypes(JsonElement plan) =>
        Nodes(plan).Select(node => node.GetProperty("Node Type").GetString()!).ToList();

    private static long RowsScanned(JsonElement plan) =>
        Nodes(plan)
            .Where(node => node.GetProperty("Node Type").GetString()!.Contains("Scan", StringComparison.Ordinal)
                           && !node.GetProperty("Node Type").GetString()!.StartsWith("Bitmap Index", StringComparison.Ordinal))
            .Sum(node =>
                (node.GetProperty("Actual Rows").GetInt64()
                 + (node.TryGetProperty("Rows Removed by Filter", out var removed) ? removed.GetInt64() : 0))
                * node.GetProperty("Actual Loops").GetInt64());

    private static string Literal(DateTime value) => value.ToString("yyyy-MM-dd HH:mm:ss.ffffff") + "+00";

    private static IEnumerable<JsonElement> Nodes(JsonElement plan)
    {
        yield return plan;

        if (plan.TryGetProperty("Plans", out var children))
        {
            foreach (var child in children.EnumerateArray().SelectMany(Nodes))
            {
                yield return child;
            }
        }
    }

    private static async Task ExecAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 300 };

        await command.ExecuteNonQueryAsync();
    }

    private static string WithDatabase(string connectionString, string database) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Database = database, CommandTimeout = 300 }.ConnectionString;
}
