using System.Globalization;
using Blog.StreamLab.Listeners;

namespace Blog.StreamLab;

public sealed record RunOptions(
    Guid RunId,
    int Listeners,
    int Seconds,
    int Repetitions,
    int WarmupSeconds,
    int BrowserListeners,
    IReadOnlyList<Contestant> Contestants);

public sealed class Runner
{
    private static readonly Dictionary<string, string> NetemNames = new()
    {
        ["blog-caligo-1"] = "caligo",
        ["blog-relay-1"] = "relay",
        ["blog-icecast-1"] = "icecast",
        ["blog-mediamtx-1"] = "mediamtx",
        ["blog-lab-web-1"] = "web",
        ["blog-lab-ftp-1"] = "ftp"
    };

    private readonly LabSettings _settings;
    private readonly SemaphoreSlim _browserGate = new(1, 1);
    private ChromeHost? _chrome;

    public Runner(LabSettings settings)
    {
        _settings = settings;
    }

    public async Task<int> RunAsync(Scenario scenario, RunOptions options)
    {
        var store = await ResultStore.OpenAsync(_settings.ClickHouse);
        await using var clock = new SourceClock(_settings.ReferencePort);
        var netem = new NetemControl(_settings.Control);
        var started = DateTime.UtcNow;
        var summary = new List<(Contestant Contestant, List<ResultRecord> Results)>();

        Console.WriteLine($"run {options.RunId}: {scenario.Name} ({scenario.Description})");
        Console.WriteLine($"{options.Contestants.Count} contestants, {options.Listeners} listeners for {options.Seconds} s, " +
                          $"{options.Repetitions} repetition(s)");

        var bias = await clock.WaitForBiasAsync(TimeSpan.FromSeconds(15));

        Console.WriteLine(bias is { } offset
            ? $"the source's reference signal is in: its beeps leave {offset:0} ms after each second"
            : "no reference signal from the source; latencies will be missing");

        var sidecars = await netem.ApplyAsync(scenario.Profile, TimeSpan.FromSeconds(10));

        Console.WriteLine($"impairment \"{scenario.Profile}\" applied by {sidecars.Count} sidecar(s): {string.Join(", ", sidecars)}");

        if (scenario.Profile != "off" && sidecars.Count == 0)
        {
            Console.Error.WriteLine("no netem sidecar is running, so the scenario cannot be applied");

            return 2;
        }

        var context = new LabContext
        {
            Settings = _settings,
            Clock = clock,
            Browser = BrowserAsync,
            Seconds = options.Seconds
        };

        try
        {
            for (var repetition = 1; repetition <= options.Repetitions; repetition++)
            {
                var random = new Random(options.RunId.GetHashCode() + repetition);

                foreach (var contestant in options.Contestants.OrderBy(_ => random.Next()))
                {
                    if (options.WarmupSeconds > 0)
                    {
                        await WarmUpAsync(contestant, context, options.WarmupSeconds);
                    }

                    var netemName = NetemNames.GetValueOrDefault(contestant.Container);
                    var before = netemName is null ? null : netem.Stats(netemName);
                    var windowStarted = DateTime.UtcNow;
                    var count = contestant.Browser ? Math.Min(options.Listeners, options.BrowserListeners) : options.Listeners;
                    var sessions = await ListenAsync(contestant, context, count, options.Seconds, probe: true);
                    var windowFinished = DateTime.UtcNow;
                    var after = netemName is null ? null : netem.Stats(netemName);
                    var results = sessions.Select(session => Record(options.RunId, contestant, repetition, session)).ToList();
                    var latencies = sessions.SelectMany(session => session.Latencies.Select(sample => new LatencyRecord(
                        options.RunId, contestant.Name, repetition, DateTime.UnixEpoch.AddMilliseconds(sample.WallMs),
                        sample.LatencyMs, sample.DataLatencyMs))).ToList();

                    await store.WriteWindowAsync(new WindowRecord(options.RunId, contestant, repetition, windowStarted, windowFinished,
                        (after?.Packets ?? 0) - (before?.Packets ?? 0), (after?.Lost ?? 0) - (before?.Lost ?? 0),
                        (after?.Overflowed ?? 0) - (before?.Overflowed ?? 0)));
                    await store.WriteResultsAsync(results);
                    await store.WriteLatencyAsync(latencies);

                    summary.Add((contestant, results));
                    Console.WriteLine(Line(contestant, repetition, results));
                }
            }
        }
        finally
        {
            await netem.ApplyAsync("off", TimeSpan.FromSeconds(5));

            if (_chrome is not null)
            {
                await _chrome.DisposeAsync();
            }
        }

        await store.WriteRunAsync(new RunRecord(options.RunId, scenario.Name, scenario.Profile, started, DateTime.UtcNow,
            options.Listeners, options.Seconds, options.Repetitions, options.Contestants.Count, clock.BiasMs,
            $"{sidecars.Count} netem sidecars; warm-up {options.WarmupSeconds} s; browser listeners {options.BrowserListeners}"));

        Console.WriteLine();
        Console.WriteLine(Table(summary));

        var silent = summary.GroupBy(item => item.Contestant.Name)
            .Where(group => group.SelectMany(item => item.Results).All(result => !result.Connected))
            .Select(group => group.Key)
            .ToList();

        if (silent.Count > 0)
        {
            Console.Error.WriteLine("no listener could connect to: " + string.Join(", ", silent));

            return 1;
        }

        return 0;
    }

    private async Task<ChromeHost> BrowserAsync(CancellationToken cancellationToken)
    {
        await _browserGate.WaitAsync(cancellationToken);

        try
        {
            return _chrome ??= await ChromeHost.StartAsync(_settings.Browser, _settings.ProbeOrigin, cancellationToken);
        }
        finally
        {
            _browserGate.Release();
        }
    }

    private static async Task WarmUpAsync(Contestant contestant, LabContext context, int seconds)
    {
        var session = new ListenerSession(0, null);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 15));
        var listening = Task.Run(async () =>
        {
            try
            {
                await contestant.Create(context).RunAsync(session, stop.Token);
            }
            catch (Exception)
            {
            }
        });
        var deadline = DateTime.UtcNow.AddSeconds(15);

        while (session.FirstByteMs is null && DateTime.UtcNow < deadline && !listening.IsCompleted)
        {
            await Task.Delay(100);
        }

        await Task.WhenAny(listening, Task.Delay(TimeSpan.FromSeconds(seconds)));
        stop.Cancel();
        await listening;
    }

    private async Task<List<ListenerSession>> ListenAsync(Contestant contestant, LabContext context, int count, int seconds, bool probe)
    {
        var sessions = new ListenerSession?[count];
        var ramp = TimeSpan.FromSeconds(Math.Min(2.0, seconds / 5.0));
        var tasks = Enumerable.Range(0, count).Select(index => Task.Run(async () =>
        {
            await Task.Delay(ramp * index / Math.Max(1, count));

            var decoder = probe && index == 0 && contestant.Live && !contestant.Browser
                ? new ProbeDecoder(_settings.Ffmpeg, context.Clock)
                : null;
            var session = new ListenerSession(index, decoder);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));

            sessions[index] = session;

            try
            {
                await contestant.Create(context).RunAsync(session, stop.Token);

                if (!stop.IsCancellationRequested && !contestant.Browser && !contestant.Finite)
                {
                    session.Fail("the stream ended before the window closed");
                }
            }
            catch (Exception error)
            {
                session.Fail(error);
            }
            finally
            {
                if (!session.Connected)
                {
                    session.Fail($"not connected within the {seconds} s window");
                }

                session.Finish();

                if (decoder is not null)
                {
                    await decoder.FinishAsync(session);
                    await decoder.DisposeAsync();
                }
            }
        })).ToList();

        await Task.WhenAll(tasks);

        return sessions.Where(session => session is not null).Cast<ListenerSession>().ToList();
    }

    private static ResultRecord Record(Guid runId, Contestant contestant, int repetition, ListenerSession session)
    {
        var latencies = session.Latencies.Select(sample => sample.LatencyMs).ToList();
        var dataLatencies = session.Latencies.Where(sample => sample.DataLatencyMs is not null)
            .Select(sample => sample.DataLatencyMs!.Value).ToList();

        return new ResultRecord(
            runId,
            contestant.Name,
            repetition,
            session.Index,
            session.Probe is not null || (contestant.Browser && contestant.Live),
            DateTime.UtcNow,
            session.Connected,
            session.FirstByteOverrideMs ?? session.FirstByteMs,
            session.StartupOverrideMs ?? session.Model.StartedAt,
            session.StallsOverride ?? session.Model.Stalls,
            session.StallMsOverride ?? session.Model.StallMs,
            session.MediaOverrideMs ?? session.Model.ArrivedMs,
            session.Bytes,
            Median(latencies),
            Median(dataLatencies),
            session.Error ?? string.Empty);
    }

    public static double? Median(IReadOnlyCollection<double> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var sorted = values.OrderBy(value => value).ToList();

        return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }

    private static string Line(Contestant contestant, int repetition, IReadOnlyList<ResultRecord> results)
    {
        var connected = results.Count(result => result.Connected);
        var startup = Median(results.Where(result => result.StartupMs is not null).Select(result => result.StartupMs!.Value).ToList());
        var latency = Median(results.Where(result => result.LatencyMs is not null).Select(result => result.LatencyMs!.Value).ToList());
        var stalls = results.Sum(result => result.Stalls);
        var errors = results.Select(result => result.Error).Where(error => error.Length > 0).Distinct().Take(2).ToList();

        return string.Format(CultureInfo.InvariantCulture,
            "  [{0}] {1,-16} {2}/{3} connected, start-up {4}, latency {5}, {6} stall(s){7}",
            repetition, contestant.Name, connected, results.Count, Milliseconds(startup), Milliseconds(latency), stalls,
            errors.Count > 0 ? "; " + string.Join(" | ", errors) : string.Empty);
    }

    private static string Table(IEnumerable<(Contestant Contestant, List<ResultRecord> Results)> summary)
    {
        var lines = new List<string>
        {
            "| contestant | protocol | connected | start-up p50 | start-up p95 | stalls / listener-min | latency p50 | data latency p50 |",
            "|---|---|---|---|---|---|---|---|"
        };

        foreach (var group in summary.GroupBy(item => item.Contestant))
        {
            var results = group.SelectMany(item => item.Results).ToList();
            var startups = results.Where(result => result.StartupMs is not null).Select(result => result.StartupMs!.Value).ToList();
            var minutes = results.Sum(result => result.MediaMs) / 60000.0;
            var stalls = results.Sum(result => result.Stalls);
            var latency = Median(results.Where(result => result.LatencyMs is not null).Select(result => result.LatencyMs!.Value).ToList());
            var data = Median(results.Where(result => result.DataLatencyMs is not null).Select(result => result.DataLatencyMs!.Value).ToList());

            lines.Add(string.Format(CultureInfo.InvariantCulture, "| {0} | {1} | {2}/{3} | {4} | {5} | {6} | {7} | {8} |",
                group.Key.Name, group.Key.Protocol, results.Count(result => result.Connected), results.Count,
                Milliseconds(Median(startups)), Milliseconds(Percentile(startups, 0.95)),
                minutes > 0 ? (stalls / minutes).ToString("0.00", CultureInfo.InvariantCulture) : "-",
                Milliseconds(latency), Milliseconds(data)));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static double? Percentile(IReadOnlyCollection<double> values, double fraction)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var sorted = values.OrderBy(value => value).ToList();

        return sorted[(int)Math.Min(sorted.Count - 1, Math.Ceiling(fraction * sorted.Count) - 1)];
    }

    private static string Milliseconds(double? value) =>
        value is { } number ? number.ToString("0", CultureInfo.InvariantCulture) + " ms" : "-";
}
