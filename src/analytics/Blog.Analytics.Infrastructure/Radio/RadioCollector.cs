using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Blog.Analytics.Core.Radio;

namespace Blog.Analytics.Infrastructure.Radio
{
    public class RadioOptions
    {
        public const string SectionName = "Radio";

        public string EventsUrl { get; set; } = string.Empty;

        public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(5);

        public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(45);

        public string StatsUrl { get; set; } = string.Empty;

        public string StatsSource { get; set; } = "live";

        public TimeSpan SampleInterval { get; set; } = TimeSpan.FromMinutes(1);

        public bool Enabled => !string.IsNullOrWhiteSpace(EventsUrl);

        public bool SamplesListeners => !string.IsNullOrWhiteSpace(StatsUrl);
    }

    public class RadioCollector : BackgroundService
    {
        private const string DataPrefix = "data:";

        private readonly RadioOptions _options;
        private readonly IRadioStore _store;
        private readonly TimeProvider _time;
        private readonly ILogger<RadioCollector> _logger;
        private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };

        private RadioPlay? _last;

        public RadioCollector(
            IOptions<RadioOptions> options, IRadioStore store, TimeProvider time, ILogger<RadioCollector> logger)
        {
            _options = options.Value;
            _store = store;
            _time = time;
            _logger = logger;
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_store.IsConfigured || (!_options.Enabled && !_options.SamplesListeners))
            {
                _logger.LogInformation("The radio is not recorded: no Radio URLs or no ClickHouse configured.");

                return Task.CompletedTask;
            }

            return Task.WhenAll(
                _options.Enabled ? FollowPlaysAsync(stoppingToken) : Task.CompletedTask,
                _options.SamplesListeners ? SampleListenersAsync(stoppingToken) : Task.CompletedTask);
        }

        private async Task SampleListenersAsync(CancellationToken stoppingToken)
        {
            var failures = 0;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

                    timeout.CancelAfter(TimeSpan.FromSeconds(10));

                    await using var body = await _http.GetStreamAsync(_options.StatsUrl, timeout.Token);

                    using var document = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token);

                    if (document.RootElement.ValueKind == JsonValueKind.Object &&
                        document.RootElement.TryGetProperty("listeners", out var listeners) &&
                        listeners.TryGetInt32(out var count))
                    {
                        await _store.WriteListenersAsync(
                            new ListenerSample(_time.GetUtcNow().UtcDateTime, _options.StatsSource, count), stoppingToken);
                    }

                    failures = 0;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (failures++ % 60 == 0)
                    {
                        _logger.LogWarning(ex, "Recording listener counts from {Url} failed; trying again.", _options.StatsUrl);
                    }
                }

                try
                {
                    await Task.Delay(_options.SampleInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private async Task FollowPlaysAsync(CancellationToken stoppingToken)
        {
            var known = false;
            var failures = 0;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (!known)
                    {
                        _last = await _store.LatestPlayAsync(stoppingToken);
                        known = true;
                    }

                    await FollowAsync(stoppingToken);

                    failures = 0;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (failures++ % 60 == 0)
                    {
                        _logger.LogWarning(ex, "Recording the radio from {Url} failed; trying again.", _options.EventsUrl);
                    }
                }

                try
                {
                    await Task.Delay(_options.ReconnectDelay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private async Task FollowAsync(CancellationToken stoppingToken)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

            idle.CancelAfter(_options.IdleTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, _options.EventsUrl);

            request.Headers.Accept.ParseAdd("text/event-stream");

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, idle.Token);

            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(idle.Token);

            using var reader = new StreamReader(stream);

            _logger.LogInformation("Following the radio at {Url}.", _options.EventsUrl);

            try
            {
                while (await reader.ReadLineAsync(idle.Token) is { } line)
                {
                    idle.CancelAfter(_options.IdleTimeout);

                    if (!line.StartsWith(DataPrefix, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var play = NowPlaying.Parse(line[DataPrefix.Length..].Trim(), _time.GetUtcNow().UtcDateTime);

                    if (play is null || play.ContinuesFrom(_last))
                    {
                        continue;
                    }

                    await _store.WritePlayAsync(play, stoppingToken);

                    _last = play;
                }
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("No radio event for {Idle}; reconnecting.", _options.IdleTimeout);
            }
        }

        public override void Dispose()
        {
            _http.Dispose();

            base.Dispose();
        }
    }
}
