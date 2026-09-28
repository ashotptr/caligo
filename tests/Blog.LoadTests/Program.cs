using Blog.LoadTests.Scenarios;

namespace Blog.LoadTests;

public static class Program
{
    private static readonly Dictionary<string, Func<LoadTestSettings, CommandOptions, Task<Summary>>> Scenarios = new()
    {
        ["hot-row"] = HotRow.RunAsync,
        ["views-spike"] = ViewsSpike.RunAsync,
        ["clap-storm"] = ClapStorm.RunAsync,
        ["scheduled-burst"] = ScheduledBurst.RunAsync,
        ["fanout-spike"] = FanOutSpike.RunAsync
    };

    // dotnet run -c Release --project tests/Blog.LoadTests -- <scenario|all> [--rate 300 --seconds 30 ...]
    public static async Task<int> Main(string[] args)
    {
        var name = args.FirstOrDefault();

        if (name is null || (name != "all" && !Scenarios.ContainsKey(name)))
        {
            Console.WriteLine($"Usage: dotnet run -c Release --project tests/Blog.LoadTests -- <{string.Join("|", Scenarios.Keys)}|all> [options]");

            return 2;
        }

        var settings = new LoadTestSettings();
        var options = new CommandOptions(args.Skip(1));

        var selected = name == "all" ? Scenarios.Keys.ToList() : new List<string> { name };

        var passed = true;

        foreach (var scenario in selected)
        {
            await Db.ResetStatementsAsync(settings.PostsDb);

            var summary = await Scenarios[scenario](settings, options);

            var statements = await Db.TopStatementsAsync(settings.PostsDb, 8);

            if (statements.Count > 0)
            {
                summary.Line(string.Empty);
                summary.Line("| calls | total ms | mean ms | posts-db statement (pg_stat_statements) |");
                summary.Line("|---|---|---|---|");

                foreach (var row in statements)
                {
                    summary.Line(row);
                }
            }

            summary.Write(settings.ReportFolder);

            passed &= summary.Passed;
        }

        return passed ? 0 : 1;
    }
}
