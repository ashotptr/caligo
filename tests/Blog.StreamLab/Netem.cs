using System.Text.Json;

namespace Blog.StreamLab;

public sealed record NetemStats(long Packets, long Lost, long Overflowed, long Sent);

public sealed class NetemControl
{
    private const string Control = "netem.conf";

    private readonly string _folder;

    public NetemControl(string folder)
    {
        _folder = folder;
    }

    public IReadOnlyList<string> Sidecars()
    {
        if (!Directory.Exists(_folder))
        {
            return Array.Empty<string>();
        }

        return Directory.GetFiles(_folder, Control + ".*.stats")
            .Where(path => File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddSeconds(-10))
            .Select(path => Path.GetFileName(path)[(Control.Length + 1)..^".stats".Length])
            .OrderBy(name => name)
            .ToList();
    }

    public async Task<IReadOnlyList<string>> ApplyAsync(string profile, TimeSpan timeout)
    {
        var sidecars = Sidecars();
        var written = DateTime.UtcNow.AddMilliseconds(-50);
        var temporary = Path.Combine(_folder, Control + ".part");

        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(temporary, profile);
        File.Move(temporary, Path.Combine(_folder, Control), overwrite: true);

        var deadline = DateTime.UtcNow + timeout;
        var applied = new List<string>();

        while (DateTime.UtcNow < deadline)
        {
            applied = sidecars.Where(name => File.GetLastWriteTimeUtc(Path.Combine(_folder, $"{Control}.{name}.applied")) >= written).ToList();

            var refused = sidecars.Where(name => File.GetLastWriteTimeUtc(Path.Combine(_folder, $"{Control}.{name}.error")) >= written).ToList();

            if (refused.Count > 0)
            {
                throw new InvalidOperationException($"netem refused the profile \"{profile}\" in {string.Join(", ", refused)}");
            }

            if (applied.Count == sidecars.Count)
            {
                break;
            }

            await Task.Delay(100);
        }

        return applied;
    }

    public NetemStats? Stats(string sidecar)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(_folder, $"{Control}.{sidecar}.stats")));
            var root = document.RootElement;

            return new NetemStats(
                root.GetProperty("packets").GetInt64(),
                root.GetProperty("lost").GetInt64(),
                root.GetProperty("overflowed").GetInt64(),
                root.GetProperty("sent").GetInt64());
        }
        catch (Exception)
        {
            return null;
        }
    }
}
