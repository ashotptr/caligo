using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Caligo.Catalog.Application.Abstractions;
using Caligo.SharedKernel;

namespace Caligo.Catalog.Application.Engine;

public partial class CaligoService
{
    public const int MaxQueryLength = 500;

    private readonly ICaligoEngine _engine;
    private readonly ICatalogViewer _viewer;
    private readonly IMixtapeLookup _mixtape;
    private readonly IMusicSync _sync;

    public CaligoService(ICaligoEngine engine, ICatalogViewer viewer, IMixtapeLookup mixtape, IMusicSync sync)
    {
        _engine = engine;
        _viewer = viewer;
        _mixtape = mixtape;
        _sync = sync;
    }

    public Result<IAsyncEnumerable<string>> Ingest(string? query, Caller caller)
    {
        if (!_engine.IsConfigured)
        {
            return Result<IAsyncEnumerable<string>>.Unavailable("Caligo isn't connected here.");
        }

        if (!caller.IsAuthenticated || _viewer.UserName is not { Length: > 0 } user)
        {
            return Result<IAsyncEnumerable<string>>.Unauthorized("ACCESS DENIED: LOGIN REQUIRED");
        }

        var cleaned = query?.Trim();

        if (string.IsNullOrEmpty(cleaned) || cleaned.Length > MaxQueryLength)
        {
            return Result<IAsyncEnumerable<string>>.Invalid($"Send a Spotify link or a search of up to {MaxQueryLength} characters.");
        }

        return Result<IAsyncEnumerable<string>>.Ok(Synced(_engine.RequestSongAsync(user, cleaned)));
    }

    public async Task<Result<RadioQueueResult>> QueueAsync(
        QueueTrackCommand command, Caller caller, CancellationToken cancellationToken = default)
    {
        if (!_engine.IsConfigured)
        {
            return Result<RadioQueueResult>.Unavailable("Caligo isn't connected here.");
        }

        var hidden = await _viewer.HiddenAsync(cancellationToken);

        if (hidden.ContentIds.Contains(command.ContentId) ||
            await _mixtape.TrackAsync(command.ContentId, cancellationToken) is not { } track)
        {
            return Result<RadioQueueResult>.NotFound("Only songs from the Thesaurarium's albums can go to the live radio.");
        }

        var result = await _engine.QueueToRadioAsync(
            new RadioQueueRequest($"{track.Folder}/{track.File}", track.Artist, track.Album, track.Title),
            cancellationToken);

        return Result<RadioQueueResult>.Ok(result);
    }

    public async Task<Result<AskAnswer>> AskAsync(
        AskCommand command, Caller caller, CancellationToken cancellationToken = default)
    {
        if (!_engine.IsConfigured)
        {
            return Result<AskAnswer>.Unavailable("Caligo isn't connected here.");
        }

        var query = command.Query?.Trim();

        if (string.IsNullOrEmpty(query) || query.Length > MaxQueryLength)
        {
            return Result<AskAnswer>.Invalid($"Ask something of up to {MaxQueryLength} characters.");
        }

        string session;

        if (caller.UserId is { } userId)
        {
            session = "user-" + userId;
        }
        else if (command.SessionId is { } browser && BrowserSession().IsMatch(browser))
        {
            session = browser;
        }
        else
        {
            return Result<AskAnswer>.Invalid("Send the session id this browser uses for the Omniscient AI.");
        }

        var answer = await _engine.AskAsync(session, query, cancellationToken);

        return answer is null ? Result<AskAnswer>.Unavailable("Connection Failed") : Result<AskAnswer>.Ok(answer);
    }

    public async Task<Result<IAsyncEnumerable<string>>> RotationAsync(
        RotationCommand command, Caller caller, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdministrator)
        {
            return Result<IAsyncEnumerable<string>>.Forbidden();
        }

        if (!_engine.IsConfigured)
        {
            return Result<IAsyncEnumerable<string>>.Unavailable("Caligo isn't connected here.");
        }

        var hidden = await _viewer.HiddenAsync(cancellationToken);
        RotationRequest? request = null;

        if (command.ContentId is { } contentId)
        {
            if (!hidden.ContentIds.Contains(contentId) && await _mixtape.TrackAsync(contentId, cancellationToken) is { } track)
            {
                request = new RotationRequest(track.Folder, track.File, track.Artist, track.Album, track.Title);
            }
        }
        else if (command.AlbumId is { } albumId)
        {
            if (!hidden.AlbumIds.Contains(albumId) && await _mixtape.AlbumFolderAsync(albumId, cancellationToken) is { } folder)
            {
                request = new RotationRequest(folder, null, null, null, null);
            }
        }
        else if (command.SingleId is { } singleId)
        {
            if (!hidden.SingleIds.Contains(singleId) && await _mixtape.SingleFolderAsync(singleId, cancellationToken) is { } folder)
            {
                request = new RotationRequest(folder, null, null, null, null);
            }
        }

        return request is null
            ? Result<IAsyncEnumerable<string>>.NotFound("Only the Thesaurarium's own albums and their songs can join the radio rotation.")
            : Result<IAsyncEnumerable<string>>.Ok(Synced(_engine.AddToRotationAsync(request)));
    }

    private async IAsyncEnumerable<string> Synced(
        IAsyncEnumerable<string> lines, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        try
        {
            await foreach (var line in lines.WithCancellation(cancellationToken))
            {
                yield return line;
            }
        }
        finally
        {
            _sync.Request();
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex BrowserSession();
}
