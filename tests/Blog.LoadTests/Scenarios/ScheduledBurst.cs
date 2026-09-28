using Npgsql;
using NpgsqlTypes;

namespace Blog.LoadTests.Scenarios;

// Thousands of posts scheduled for the same second, the top-of-the-hour burst. Measures how long
// the scheduled publishers (one per API replica) take to publish all of them and how long the
// outbox takes to deliver their post.published events, and checks each was announced exactly once.
public static class ScheduledBurst
{
    public static async Task<Summary> RunAsync(LoadTestSettings settings, CommandOptions options)
    {
        var posts = options.Int("posts", 2000);
        var lead = TimeSpan.FromSeconds(options.Int("lead", 15));

        var summary = new Summary("scheduled-burst");

        var subscribers = await Db.ScalarAsync(settings.NotificationsDb, "SELECT count(*) FROM subscribers WHERE \"IsActive\"");

        if (subscribers > 1000)
        {
            Console.WriteLine($"Warning: {subscribers} active subscribers; every burst post fans out to all of them.");
        }

        var run = Guid.NewGuid().ToString("N")[..8];
        var due = DateTime.UtcNow + lead;

        await SchedulePostsAsync(settings, run, posts, due);

        var titles = $"\"Title\" LIKE 'burst-{run}-%'";
        var ids = $"SELECT \"Id\"::text FROM \"BlogPosts\" WHERE {titles}";

        var livePosts = $"SELECT count(*) FROM \"BlogPosts\" WHERE {titles} AND \"Status\" = 0";
        var announced = $"SELECT count(*) FROM outbox_messages WHERE \"EventType\" = 'post.published' AND \"PartitionKey\" IN ({ids})";
        var delivered = $"{announced} AND \"ProcessedAt\" IS NOT NULL";

        await Task.Delay(due - DateTime.UtcNow + TimeSpan.FromMilliseconds(50));

        TimeSpan? allLive = null;
        TimeSpan? allDelivered = null;

        while (allDelivered is null && DateTime.UtcNow - due < TimeSpan.FromMinutes(5))
        {
            if (allLive is null && await Db.TryScalarAsync(settings.PostsDb, livePosts) >= posts)
            {
                allLive = DateTime.UtcNow - due;
            }

            if (await Db.TryScalarAsync(settings.PostsDb, delivered) >= posts)
            {
                allDelivered = DateTime.UtcNow - due;
            }

            await Task.Delay(100);
        }

        var duplicates = await Db.ScalarAsync(settings.PostsDb, $"""
            SELECT count(*) FROM (
                SELECT "PartitionKey" FROM outbox_messages
                WHERE "EventType" = 'post.published' AND "PartitionKey" IN ({ids})
                GROUP BY "PartitionKey" HAVING count(*) > 1) AS twice
            """);

        var announcements = await Db.ScalarAsync(settings.PostsDb, announced);

        summary.Line($"| posts due at the same second | {posts} |");
        summary.Line($"| all published | {Seconds(allLive)} after they were due |");
        summary.Line($"| all post.published events on Kafka | {Seconds(allDelivered)} after they were due |");
        summary.Line(allLive is { } live ? $"| publish rate | {posts / Math.Max(live.TotalSeconds, 0.001):0} posts/s |" : "| publish rate | n/a |");

        summary.Check("every post published", allLive is not null, Seconds(allLive));
        summary.Check("one post.published per post, no duplicates", announcements == posts && duplicates == 0,
            $"{announcements} events, {duplicates} post(s) announced twice");

        return summary;
    }

    // Inserted straight into the table: the burst being measured is the publishing, not 2,000 API
    // calls to create the posts.
    private static async Task SchedulePostsAsync(LoadTestSettings settings, string run, int count, DateTime due)
    {
        var authorId = await AdminIdAsync(settings);

        await using var connection = new NpgsqlConnection(settings.PostsDb);
        await connection.OpenAsync();

        await using var writer = await connection.BeginBinaryImportAsync("""
            COPY "BlogPosts" ("Title", "Content", "PublishedDate", "AuthorId", "Tags", "Status", "ScheduledFor", "Version")
            FROM STDIN (FORMAT BINARY)
            """);

        for (var i = 0; i < count; i++)
        {
            await writer.StartRowAsync();
            await writer.WriteAsync($"burst-{run}-{i}", NpgsqlDbType.Varchar);
            await writer.WriteAsync("Scheduled by the load tests.", NpgsqlDbType.Text);
            await writer.WriteAsync(due, NpgsqlDbType.TimestampTz);
            await writer.WriteAsync(authorId, NpgsqlDbType.Text);
            await writer.WriteAsync(new[] { "loadtest" }, NpgsqlDbType.Array | NpgsqlDbType.Text);
            await writer.WriteAsync(1, NpgsqlDbType.Integer);
            await writer.WriteAsync(due, NpgsqlDbType.TimestampTz);
            await writer.WriteAsync(0, NpgsqlDbType.Integer);
        }

        await writer.CompleteAsync();
    }

    private static async Task<string> AdminIdAsync(LoadTestSettings settings)
    {
        await using var connection = new NpgsqlConnection(settings.PostsDb);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "SELECT \"Id\" FROM \"AspNetUsers\" WHERE \"NormalizedUserName\" = upper(@name)", connection);

        command.Parameters.AddWithValue("name", settings.AdminUser);

        return (string?)await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException($"No user {settings.AdminUser}; is the stack seeded?");
    }

    private static string Seconds(TimeSpan? value) => value is { } v ? $"{v.TotalSeconds:0.00} s" : "not reached";
}
