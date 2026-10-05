using System.Net.Http.Json;
using NBomber.CSharp;
using NBomber.Http.CSharp;

namespace Blog.LoadTests.Scenarios;

public static class ClapStorm
{
    public static async Task<Summary> RunAsync(LoadTestSettings settings, CommandOptions options)
    {
        var rate = options.Int("rate", 100);
        var seconds = options.Int("seconds", 30);
        var readerCount = options.Int("readers", 500);

        var summary = new Summary("clap-storm");

        using var api = new BlogApi(settings);

        var admin = await api.LoginAdminAsync();
        var postId = await api.CreatePostAsync(admin, $"clap-storm {Guid.NewGuid():N}");

        var run = Guid.NewGuid().ToString("N")[..8];
        var readers = Enumerable.Range(0, readerCount)
            .Select(i => api.MintToken($"loadtest-{run}-{i}", "Reader"))
            .ToArray();

        var backlog = $"SELECT count(*) FROM clap_deltas WHERE \"PostId\" = {postId}";

        using var sampling = new CancellationTokenSource();

        var largestBacklog = 0L;

        var sampler = Task.Run(async () =>
        {
            while (!sampling.IsCancellationRequested)
            {
                largestBacklog = Math.Max(largestBacklog, await Db.TryScalarAsync(settings.PostsDb, backlog) ?? 0);

                try
                {
                    await Task.Delay(250, sampling.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }
        });

        var scenario = Scenario.Create("clap_storm", async _ =>
        {
            var request = Http.CreateRequest("POST", $"/api/BlogPosts/{postId}/claps")
                .WithHeader("Authorization", $"Bearer {readers[Random.Shared.Next(readers.Length)]}")
                .WithBody(JsonContent.Create(new { count = Random.Shared.Next(1, 11) }));

            return await Http.Send(api.Http, request);
        })
        .WithoutWarmUp()
        .WithLoadSimulations(
            Simulation.Inject(rate: rate, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(seconds)));

        var stats = Runner.Run(settings, scenario);

        var drained = await Db.WaitUntilAsync(
            async () => await Db.ScalarAsync(settings.PostsDb, backlog) == 0, TimeSpan.FromMinutes(2));

        sampling.Cancel();
        await sampler;

        var loudest = await Db.ScalarAsync(
            settings.PostsDb, $"SELECT coalesce(max(\"Claps\"), 0) FROM post_claps WHERE \"PostId\" = {postId}");
        var capped = await Db.ScalarAsync(
            settings.PostsDb, $"SELECT count(*) FROM post_claps WHERE \"PostId\" = {postId} AND \"Claps\" = 50");
        var accepted = await Db.ScalarAsync(
            settings.PostsDb, $"SELECT coalesce(sum(\"Claps\"), 0) FROM post_claps WHERE \"PostId\" = {postId}");
        var rolledUp = await Db.ScalarAsync(
            settings.PostsDb, $"SELECT coalesce(max(\"Claps\"), 0) FROM post_clap_totals WHERE \"PostId\" = {postId}");

        var audit = await api.GetJsonAsync(admin, $"/api/BlogPosts/claps/audit?postId={postId}");
        var mismatches = audit.GetProperty("mismatches").GetArrayLength();

        await Db.WaitUntilAsync(
            async () => await ClickHouseClapsAsync(settings, postId) >= accepted, TimeSpan.FromMinutes(1));

        var analysed = await ClickHouseClapsAsync(settings, postId);

        summary.Latency(stats);
        summary.Line($"| readers | {readerCount} |");
        summary.Line($"| claps accepted | {accepted} |");
        summary.Line($"| readers who reached the 50-clap cap | {capped} |");
        summary.Line($"| largest rollup backlog | {largestBacklog} deltas |");
        summary.Line($"| backlog drained after the load stopped | {(drained is { } d ? $"{d.TotalSeconds:0.0} s" : "not within 2 min")} |");
        summary.Line($"| claps in ClickHouse | {analysed} of {accepted} |");

        summary.Check("error rate under 1%", Runner.UnderOnePercentErrors(stats), Runner.ErrorRate(stats));
        summary.Check("the load reached the cap, so the next check means something", capped > 0, $"{capped} reader(s) at 50");
        summary.Check("no reader above the 50-clap cap", loudest <= 50, $"highest reader: {loudest}");
        summary.Check("post total equals the per-reader sum", rolledUp == accepted, $"{rolledUp} vs {accepted}");
        summary.Check("audit finds no mismatch", mismatches == 0, $"{mismatches} mismatch(es)");

        return summary;
    }

    private static Task<long> ClickHouseClapsAsync(LoadTestSettings settings, int postId) =>
        Db.ClickHouseScalarAsync(settings.ClickHouse, $"SELECT sum(delta) FROM clap_events FINAL WHERE post_id = {postId}");
}
