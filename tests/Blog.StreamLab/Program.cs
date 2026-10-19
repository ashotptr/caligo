namespace Blog.StreamLab;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var name = args.FirstOrDefault();

        if (name == "schema")
        {
            await ResultStore.OpenAsync(new LabSettings().ClickHouse);
            Console.WriteLine("the lab tables are in place");

            return 0;
        }

        if (name is null || name == "list" || !Scenarios.All.ContainsKey(name))
        {
            Console.WriteLine("Usage: Blog.StreamLab <scenario> [--listeners N] [--seconds S] [--repetitions R] [--warmup W]");
            Console.WriteLine("       [--browser-listeners N] [--contestants a,b,c] [--run-id GUID] [--quick]");
            Console.WriteLine();
            Console.WriteLine("Scenarios:");

            foreach (var scenario in Scenarios.All.Values)
            {
                Console.WriteLine($"  {scenario.Name,-14} {scenario.Description}");
            }

            Console.WriteLine();
            Console.WriteLine("Contestants:");

            foreach (var contestant in Contestants.All)
            {
                Console.WriteLine($"  {contestant.Name,-16} {contestant.Protocol,-14} on {contestant.Server}");
            }

            return name == "list" ? 0 : 2;
        }

        var options = new CommandOptions(args.Skip(1));
        var quick = options.Has("quick");
        var wanted = options.Text("contestants", string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var contestants = Contestants.All.Where(contestant => wanted.Count == 0 || wanted.Contains(contestant.Name)).ToList();
        var unknown = wanted.Except(contestants.Select(contestant => contestant.Name), StringComparer.OrdinalIgnoreCase).ToList();

        if (unknown.Count > 0)
        {
            Console.Error.WriteLine("unknown contestant(s): " + string.Join(", ", unknown));

            return 2;
        }

        var run = new RunOptions(
            Guid.TryParse(options.Text("run-id", string.Empty), out var runId) ? runId : Guid.NewGuid(),
            options.Int("listeners", quick ? 3 : 20),
            options.Int("seconds", quick ? 12 : 30),
            options.Int("repetitions", quick ? 1 : 3),
            options.Int("warmup", quick ? 0 : 3),
            options.Int("browser-listeners", quick ? 2 : 4),
            contestants);

        return await new Runner(new LabSettings()).RunAsync(Scenarios.All[name], run);
    }
}
