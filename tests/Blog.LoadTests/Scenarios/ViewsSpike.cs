using NBomber.CSharp;
using NBomber.Http.CSharp;

namespace Blog.LoadTests.Scenarios;

// A new post goes viral: its cache entry starts cold and a spike of reads arrives at once.
// Measures request latency, how many of those reads reached Postgres (single-flight should hold it
// to about one per API replica), and how long until every counted view is queryable in ClickHouse.
public static class ViewsSpike
{
    // The post-detail query EF generates; nothing else selects "Version" by id during this run.
    private const string DetailQuery = "%b.\"Version\"%FROM \"BlogPosts\" AS b%WHERE b.\"Id\" = $1%";

    public static async Task<Summary> RunAsync(LoadTestSettings settings, CommandOptions options)
    {
        // A spike a laptop running the whole stack absorbs without errors; --rate for a bigger machine.
        var rate = options.Int("rate", 100);
        var seconds = options.Int("seconds", 30);

        var summary = new Summary("views-spike");

        using var api = new BlogApi(settings);

        var admin = await api.LoginAdminAsync();
        var postId = await api.CreatePostAsync(admin, $"views-spike {Guid.NewGuid():N}");

        var detailReadsBefore = await Db.StatementCallsAsync(settings.PostsDb, DetailQuery);

        var scenario = Scenario.Create("views_spike", async context =>
        {
            // Distinct user agents give distinct visitor hashes, so ClickHouse sees many readers.
            var request = Http.CreateRequest("GET", $"/api/BlogPosts/{postId}")
                .WithHeader("User-Agent", $"blog-loadtest/{context.InvocationNumber % 5000}");

            return await Http.Send(api.Http, request);
        })
        .WithoutWarmUp()
        .WithLoadSimulations(
            Simulation.RampingInject(rate: rate, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(5)),
            Simulation.Inject(rate: rate, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(seconds)));

        var stats = Runner.Run(settings, scenario);

        // A request the client gave up on may still have been served, and its view recorded, so the
        // stored count can land anywhere from the successful responses up to the requests sent.
        var counted = stats.Ok.Request.Count;
        var sent = Runner.Sent(stats);

        summary.Latency(stats);

        var detailReadsAfter = await Db.StatementCallsAsync(settings.PostsDb, DetailQuery);

        summary.Line(detailReadsBefore is { } before && detailReadsAfter is { } after
            ? $"| post-detail reads that reached Postgres | {after - before} for {counted} requests |"
            : "| post-detail reads that reached Postgres | (pg_stat_statements not loaded) |");

        var lag = await Db.WaitUntilAsync(
            async () => await ClickHouseViewsAsync(settings, postId) >= counted,
            TimeSpan.FromMinutes(2));

        var stored = await ClickHouseViewsAsync(settings, postId);

        summary.Line($"| views stored in ClickHouse | {stored} ({counted} successful responses, {sent} requests) |");
        summary.Line($"| last view queryable after the load stopped | {(lag is { } l ? $"{l.TotalSeconds:0.0} s" : "not within 2 min")} |");

        summary.Check("error rate under 1%", Runner.UnderOnePercentErrors(stats), Runner.ErrorRate(stats));
        summary.Check(
            "every served view reached ClickHouse, none twice",
            stored >= counted && stored <= sent,
            $"{counted} <= {stored} <= {sent}");

        return summary;
    }

    private static Task<long> ClickHouseViewsAsync(LoadTestSettings settings, int postId) =>
        Db.ClickHouseScalarAsync(settings.ClickHouse, $"SELECT count() FROM post_views FINAL WHERE post_id = {postId}");
}
