using NBomber.CSharp;
using NBomber.Http.CSharp;

namespace Caligo.LoadTests.Scenarios;

public static class ViewsSpike
{
    private const string DetailQuery = "%b.\"Version\"%FROM \"BlogPosts\" AS b%WHERE b.\"Id\" = $1%";

    public static async Task<Summary> RunAsync(LoadTestSettings settings, CommandOptions options)
    {
        var rate = options.Int("rate", 100);
        var seconds = options.Int("seconds", 30);

        var summary = new Summary("views-spike");

        using var api = new CaligoApi(settings);

        var admin = await api.LoginAdminAsync();
        var postId = await api.CreatePostAsync(admin, $"views-spike {Guid.NewGuid():N}");

        var detailReadsBefore = await Db.StatementCallsAsync(settings.PostsDb, DetailQuery);

        var scenario = Scenario.Create("views_spike", async context =>
        {
            var request = Http.CreateRequest("GET", $"/api/BlogPosts/{postId}")
                .WithHeader("User-Agent", $"caligo-loadtest/{context.InvocationNumber % 5000}");

            return await Http.Send(api.Http, request);
        })
        .WithoutWarmUp()
        .WithLoadSimulations(
            Simulation.RampingInject(rate: rate, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(5)),
            Simulation.Inject(rate: rate, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(seconds)));

        var stats = Runner.Run(settings, scenario);

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
