namespace Blog.LoadTests;

// Everything the load tests talk to. Defaults match deploy/compose/docker-compose.yml, so a local
// run needs no configuration; each value can be overridden with the environment variable named.
// The load-tests compose service sets them to the in-network names instead.
//
// 127.0.0.1 rather than localhost: on Windows localhost resolves to ::1 first, and WSL's relay
// accepts IPv6 connections to published ports without forwarding them, so every client hangs.
public sealed class LoadTestSettings
{
    public string SiteUrl { get; } = Env("BLOG_URL", "http://127.0.0.1:8080");

    public string AdminUser { get; } = Env("BLOG_ADMIN_USER", "admin@localhost.dev");

    public string AdminPassword { get; } = Env("BLOG_ADMIN_PASSWORD", "AdminDev04!");

    public string JwtKey { get; } = Env("BLOG_JWT_KEY", "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ");

    public string JwtIssuer { get; } = Env("BLOG_JWT_ISSUER", "blog-api");

    public string JwtAudience { get; } = Env("BLOG_JWT_AUDIENCE", "blog-client");

    public string PostsDb { get; } = Env(
        "BLOG_POSTS_DB", "Host=127.0.0.1;Port=5432;Database=blogdb;Username=postgres;Password=postgres");

    public string NotificationsDb { get; } = Env(
        "BLOG_NOTIFICATIONS_DB", "Host=127.0.0.1;Port=5432;Database=blognotifications;Username=postgres;Password=postgres");

    // A scratch database the hot-row scenario creates for itself.
    public string LabDb { get; } = Env(
        "BLOG_LAB_DB", "Host=127.0.0.1;Port=5432;Database=bloglab;Username=postgres;Password=postgres;Maximum Pool Size=300");

    public string ClickHouse { get; } = Env(
        "BLOG_CLICKHOUSE", "Host=127.0.0.1;Port=8123;Username=blog;Password=blogpass;Database=blog_analytics");

    public string Kafka { get; } = Env("BLOG_KAFKA", "127.0.0.1:9092");

    public string ReportFolder { get; } = Env("BLOG_REPORTS", Path.Combine("tests", "Blog.LoadTests", "reports"));

    private static string Env(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;
}

// "--rate 300 --seconds 30" style options after the scenario name.
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
