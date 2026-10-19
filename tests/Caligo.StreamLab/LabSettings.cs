namespace Caligo.StreamLab;

public sealed class LabSettings
{
    public string ClickHouse { get; } = Env(
        "LAB_CLICKHOUSE", "Host=127.0.0.1;Port=8123;Username=caligo;Password=caligopass;Database=caligo_analytics");

    public string Control { get; } = Env("LAB_CONTROL", "/lab-control");

    public int ReferencePort { get; } = int.Parse(Env("LAB_REFERENCE_PORT", "5099"));

    public string Ffmpeg { get; } = Env("LAB_FFMPEG", "ffmpeg");

    public string Browser { get; } = Env("LAB_BROWSER", "chromium");

    public string ProbeOrigin { get; } = Env("LAB_PROBE_ORIGIN", "http://web-lab:8080");

    private static string Env(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;
}

public sealed class CommandOptions
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public CommandOptions(IEnumerable<string> args)
    {
        var list = args.ToList();

        for (var index = 0; index < list.Count; index++)
        {
            var name = list[index].TrimStart('-');

            if (index + 1 < list.Count && !list[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                _values[name] = list[++index];
            }
            else
            {
                _values[name] = "true";
            }
        }
    }

    public int Int(string name, int fallback) =>
        _values.TryGetValue(name, out var value) && int.TryParse(value, out var parsed) ? parsed : fallback;

    public string Text(string name, string fallback) =>
        _values.TryGetValue(name, out var value) && value.Length > 0 ? value : fallback;

    public bool Has(string name) => _values.ContainsKey(name);
}
