using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Caligo.Catalog.Application.Abstractions;
using Caligo.Catalog.Application.Music;
using Caligo.Catalog.Infrastructure;
using Caligo.Catalog.Infrastructure.Engine;

namespace Caligo.Api.Catalog;

public sealed class MusicSyncService : BackgroundService, IMusicSync
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

    private readonly Channel<bool> _requests = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private readonly IServiceScopeFactory _scopes;
    private readonly IOptions<CatalogOptions> _options;
    private readonly MixtapeVisibilityCache _visibility;
    private readonly ILogger<MusicSyncService> _logger;
    private string? _fingerprint;

    public MusicSyncService(
        IServiceScopeFactory scopes,
        IOptions<CatalogOptions> options,
        MixtapeVisibilityCache visibility,
        ILogger<MusicSyncService> logger)
    {
        _scopes = scopes;
        _options = options;
        _visibility = visibility;
        _logger = logger;
    }

    public void Request() => _requests.Writer.TryWrite(true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var catalog = _options.Value;

        if (!catalog.Enabled || string.IsNullOrWhiteSpace(catalog.CaligoPublicRoot))
        {
            return;
        }

        var requested = true;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var fingerprint = Fingerprint(catalog.CaligoPublicRoot);

                if (requested || fingerprint != _fingerprint)
                {
                    await ImportAsync(stoppingToken);
                    _fingerprint = fingerprint;
                    _visibility.Forget();
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Bringing Caligo's music into the Thesaurarium failed; trying again later");
            }

            requested = await WaitAsync(stoppingToken);
        }
    }

    private async Task<bool> WaitAsync(CancellationToken stoppingToken)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        timer.CancelAfter(Every);

        try
        {
            await _requests.Reader.ReadAsync(timer.Token);

            return true;
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task ImportAsync(CancellationToken stoppingToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var music = scope.ServiceProvider.GetRequiredService<MusicImportService>();

        if (!music.IsConfigured)
        {
            return;
        }

        var result = await music.ImportAsync(stoppingToken);

        if (result.SongsAdded + result.AlbumsAdded + result.SinglesAdded + result.ArtistsAdded > 0)
        {
            _logger.LogInformation(
                "Thesaurarium now has {Albums} new albums, {Singles} new singles and {Songs} new songs from Caligo",
                result.AlbumsAdded, result.SinglesAdded, result.SongsAdded);
        }
    }

    private static string Fingerprint(string root)
    {
        var print = new StringBuilder();
        var mixtape = Path.Combine(root, "mixtape");

        if (Directory.Exists(mixtape))
        {
            foreach (var folder in Directory.GetDirectories(mixtape).Order(StringComparer.Ordinal))
            {
                print.Append(Path.GetFileName(folder)).Append(':');

                var metadata = new FileInfo(Path.Combine(folder, "metadata.json"));

                if (metadata.Exists)
                {
                    print.Append(metadata.LastWriteTimeUtc.Ticks).Append('/').Append(metadata.Length);
                }

                print.Append(';');
            }
        }

        var playlist = new FileInfo(Path.Combine(root, "music", "playlist.json"));

        if (playlist.Exists)
        {
            print.Append("playlist:").Append(playlist.LastWriteTimeUtc.Ticks).Append('/').Append(playlist.Length);
        }

        return print.ToString();
    }
}
