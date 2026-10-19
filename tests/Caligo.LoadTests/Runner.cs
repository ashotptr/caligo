using NBomber.Contracts;
using NBomber.Contracts.Stats;
using NBomber.CSharp;

namespace Caligo.LoadTests;

public static class Runner
{
    public static ScenarioStats Run(LoadTestSettings settings, ScenarioProps scenario)
    {
        var result = NBomberRunner
            .RegisterScenarios(scenario)
            .WithReportFolder(settings.ReportFolder)
            .WithReportFormats(ReportFormat.Html, ReportFormat.Md)
            .Run();

        return result.ScenarioStats.Single();
    }

    public static long Sent(ScenarioStats stats) => stats.Ok.Request.Count + stats.Fail.Request.Count;

    public static string ErrorRate(ScenarioStats stats) =>
        Sent(stats) == 0 ? "no requests" : $"{100.0 * stats.Fail.Request.Count / Sent(stats):0.00}%";

    public static bool UnderOnePercentErrors(ScenarioStats stats) =>
        Sent(stats) > 0 && stats.Fail.Request.Count * 100 < Sent(stats);
}
