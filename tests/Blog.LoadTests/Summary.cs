using System.Text;
using NBomber.Contracts.Stats;

namespace Blog.LoadTests;

public sealed class Summary
{
    private readonly List<string> _lines = new();
    private readonly List<(string Check, bool Passed, string Detail)> _checks = new();

    public Summary(string scenario)
    {
        Scenario = scenario;
    }

    public string Scenario { get; }

    public bool Passed => _checks.All(c => c.Passed);

    public void Line(string text) => _lines.Add(text);

    public void Latency(ScenarioStats stats)
    {
        var ok = stats.Ok;

        Line($"| requests | {Runner.Sent(stats)} ({stats.Fail.Request.Count} failed) |");
        Line($"| throughput | {ok.Request.RPS:0} req/s |");
        Line($"| latency p50 / p95 / p99 | {ok.Latency.Percent50:0.0} / {ok.Latency.Percent95:0.0} / {ok.Latency.Percent99:0.0} ms |");
    }

    public void Check(string check, bool passed, string detail) => _checks.Add((check, passed, detail));

    public void Write(string folder)
    {
        var text = new StringBuilder()
            .AppendLine($"## {Scenario} ({DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC)")
            .AppendLine()
            .AppendLine("| measure | value |")
            .AppendLine("|---|---|");

        foreach (var line in _lines)
        {
            text.AppendLine(line);
        }

        text.AppendLine().AppendLine("| invariant | result | detail |").AppendLine("|---|---|---|");

        foreach (var (check, passed, detail) in _checks)
        {
            text.AppendLine($"| {check} | {(passed ? "pass" : "FAIL")} | {detail} |");
        }

        Console.WriteLine();
        Console.WriteLine(text);

        Directory.CreateDirectory(folder);

        File.WriteAllText(
            Path.Combine(folder, $"summary-{Scenario}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.md"),
            text.ToString());
    }
}
