namespace Caligo.LoadTests;

public sealed class LoadTestSettings
{
    public string SiteUrl { get; } = Env("CALIGO_URL", "http://127.0.0.1:8080");

    public string AdminUser { get; } = Env("CALIGO_ADMIN_USER", "admin@localhost.dev");

    public string AdminPassword { get; } = Env("CALIGO_ADMIN_PASSWORD", "AdminDev04!");

    public string JwtKey { get; } = Env("CALIGO_JWT_KEY", "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ");

    public string JwtIssuer { get; } = Env("CALIGO_JWT_ISSUER", "caligo-api");

    public string JwtAudience { get; } = Env("CALIGO_JWT_AUDIENCE", "caligo-client");

    public string PostsDb { get; } = Env(
        "CALIGO_POSTS_DB", "Host=127.0.0.1;Port=5432;Database=caligodb;Username=postgres;Password=postgres");

    public string NotificationsDb { get; } = Env(
        "CALIGO_NOTIFICATIONS_DB", "Host=127.0.0.1;Port=5432;Database=caligonotifications;Username=postgres;Password=postgres");

    public string LabDb { get; } = Env(
        "CALIGO_LAB_DB", "Host=127.0.0.1;Port=5432;Database=caligolab;Username=postgres;Password=postgres;Maximum Pool Size=300");

    public string ClickHouse { get; } = Env(
        "CALIGO_CLICKHOUSE", "Host=127.0.0.1;Port=8123;Username=caligo;Password=caligopass;Database=caligo_analytics");

    public string Kafka { get; } = Env("CALIGO_KAFKA", "127.0.0.1:9092");

    public string ReportFolder { get; } = Env("CALIGO_REPORTS", Path.Combine("tests", "Caligo.LoadTests", "reports"));

    private static string Env(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;
}

public sealed class CommandOptions
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public CommandOptions(IEnumerable<string> args)
    {
        using var enumerator = args.GetEnumerator();

        while (enumerator.MoveNext())
        {
            var name = enumerator.Current.TrimStart('-');

            _values[name] = enumerator.MoveNext() ? enumerator.Current : "true";
        }
    }

    public int Int(string name, int fallback) =>
        _values.TryGetValue(name, out var value) && int.TryParse(value, out var parsed) ? parsed : fallback;
}
