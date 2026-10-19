using System.Text;
using Caligo.Catalog.Application.Abstractions;
using Caligo.Catalog.Application.Accounts;
using Caligo.Catalog.Domain.Media;
using Caligo.SharedKernel;

namespace Caligo.Catalog.Application.Media;

public class PlaybackService
{
    public const string NoMedia = "This title has no video yet.";
    public const string SubscriptionInactive = "Your subscription is not active. Renew it to watch.";
    public const string SessionOver = "This playback session has ended.";
    public const string CompletedOtherwise = "This session was already completed with another completion_id.";

    public const float ResumeCutoff = 0.95f;
    public const int MaxDetailLength = 200;
    public const int MaxDurationMs = 3_600_000;
    public const int MaxHeight = 10_000;

    private static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(10);

    private readonly IStreamingAccounts _accounts;
    private readonly IMediaStore _media;
    private readonly IPlaybackStore _playback;
    private readonly IPlaybackEventSink _events;
    private readonly MediaRules _rules;
    private readonly TimeProvider _time;
    private readonly byte[] _key;

    public PlaybackService(
        IStreamingAccounts accounts,
        IMediaStore media,
        IPlaybackStore playback,
        IPlaybackEventSink events,
        MediaRules rules,
        TimeProvider time)
    {
        _accounts = accounts;
        _media = media;
        _playback = playback;
        _events = events;
        _rules = rules;
        _time = time;
        _key = Encoding.UTF8.GetBytes(rules.SigningKey);
    }

    public async Task<Result<PlaybackSessionDto>> StartAsync(
        StartSessionCommand command, Caller caller, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAuthenticated)
        {
            return Result<PlaybackSessionDto>.Unauthorized();
        }

        if (!PlaybackSession.IsValidDeviceId(command.DeviceId))
        {
            return Result<PlaybackSessionDto>.Invalid(
                $"device_id must be {PlaybackSession.MinDeviceIdLength} to {PlaybackSession.MaxDeviceIdLength} letters, digits, '-' or '_'.");
        }

        var user = await _accounts.FindByIdentityAsync(caller.UserId!, cancellationToken);

        if (user is null)
        {
            return Result<PlaybackSessionDto>.Conflict(StreamingAccountService.ProfileRequired);
        }

        var media = await _media.FindAsync(command.ContentId, cancellationToken);

        if (media is null || !media.IsPlayable)
        {
            return Result<PlaybackSessionDto>.NotFound(NoMedia);
        }

        var now = Now();

        if (!user.IsSubscribedOn(DateOnly.FromDateTime(now)))
        {
            return Result<PlaybackSessionDto>.Forbidden(SubscriptionInactive);
        }

        var plan = await _accounts.FindPlanAsync(user.SubscriptionPlanId, cancellationToken);
        var limit = PlanLimits.MaxStreams(plan?.PlanInfo) ?? Math.Max(_rules.DefaultMaxStreams, 1);

        var opened = await _playback.OpenAsync(
            new OpenSession(
                user.UserId,
                command.ContentId,
                command.DeviceId!,
                now,
                now + _rules.Lease,
                limit,
                now - _rules.MaxSessionAge + _rules.Lease,
                now - _rules.SessionRetention),
            cancellationToken);

        if (opened.Session is null)
        {
            return Result<PlaybackSessionDto>.Conflict(LimitReached(limit, opened.ActiveStreams));
        }

        var position = await _playback.GetPositionAsync(user.UserId, command.ContentId, cancellationToken);

        return Result<PlaybackSessionDto>.Ok(
            await DescribeAsync(opened.Session, media, position, limit, opened.Resumed, cancellationToken));
    }

    public async Task<Result<PositionDto>> ReportPositionAsync(
        Guid sessionId, PositionCommand command, Caller caller, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAuthenticated)
        {
            return Result<PositionDto>.Unauthorized();
        }

        if (!PlaybackPosition.IsValid(command.Position) || command.Seq < 0)
        {
            return Result<PositionDto>.Invalid("position is a number of seconds from 0, and seq cannot be negative.");
        }

        var session = await OwnedAsync(sessionId, caller, cancellationToken);

        if (session is null)
        {
            return Result<PositionDto>.NotFound();
        }

        var now = Now();
        var expiresAt = Earlier(now + _rules.Lease, session.StartedAt + _rules.MaxSessionAge);

        if (!session.IsLiveAt(now) || expiresAt <= now)
        {
            return Result<PositionDto>.Gone(SessionOver);
        }

        var saved = await _playback.SavePositionAsync(
            session, command.Position, command.Seq, now, expiresAt, cancellationToken);

        return saved is null
            ? Result<PositionDto>.Gone(SessionOver)
            : Result<PositionDto>.Ok(new PositionDto(
                saved.Position, saved.Seq, Utc(saved.ExpiresAt), SecondsUntil(saved.ExpiresAt, now)));
    }

    public async Task<Result<CompletionDto>> CompleteAsync(
        Guid sessionId, CompleteCommand command, Caller caller, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAuthenticated)
        {
            return Result<CompletionDto>.Unauthorized();
        }

        if (command.CompletionId == Guid.Empty)
        {
            return Result<CompletionDto>.Invalid("completion_id must be a UUID.");
        }

        if (command.Position is { } position && !PlaybackPosition.IsValid(position))
        {
            return Result<CompletionDto>.Invalid("position is a number of seconds from 0.");
        }

        var session = await OwnedAsync(sessionId, caller, cancellationToken);

        if (session is null)
        {
            return Result<CompletionDto>.NotFound();
        }

        var now = Now();

        if (!session.IsCompleted && now - session.StartedAt > _rules.MaxSessionAge + _rules.Lease)
        {
            return Result<CompletionDto>.Gone(SessionOver);
        }

        var completion = await _playback.CompleteAsync(
            sessionId, session.UserId, command.CompletionId, command.Position, now, cancellationToken);

        return completion.Outcome switch
        {
            CompletionOutcome.Missing => Result<CompletionDto>.NotFound(),
            CompletionOutcome.Mismatch => Result<CompletionDto>.Conflict(CompletedOtherwise),
            _ => Result<CompletionDto>.Ok(new CompletionDto(
                completion.Session!.CompletionId!.Value,
                completion.Session.ContentId,
                completion.Session.WatchedContentId,
                Utc(completion.Session.CompletedAt ?? now),
                completion.Outcome == CompletionOutcome.Repeated))
        };
    }

    public async Task<Result> EndAsync(Guid sessionId, Caller caller, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAuthenticated)
        {
            return Result.Unauthorized();
        }

        var userId = await _accounts.FindUserIdAsync(caller.UserId!, cancellationToken);

        if (userId is null)
        {
            return Result.NotFound();
        }

        return await _playback.EndAsync(sessionId, userId.Value, Now(), cancellationToken)
            ? Result.Ok()
            : Result.NotFound();
    }

    public async Task<Result<EventsAcceptedDto>> RecordEventsAsync(
        Guid sessionId, IReadOnlyList<PlaybackEventCommand>? events, Caller caller,
        CancellationToken cancellationToken = default)
    {
        if (!caller.IsAuthenticated)
        {
            return Result<EventsAcceptedDto>.Unauthorized();
        }

        if (events is null || events.Count > MediaRules.MaxEventsPerBatch)
        {
            return Result<EventsAcceptedDto>.Invalid(
                $"Send up to {MediaRules.MaxEventsPerBatch} events in one batch.");
        }

        var session = await OwnedAsync(sessionId, caller, cancellationToken);

        if (session is null)
        {
            return Result<EventsAcceptedDto>.NotFound();
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var accepted = new List<PlaybackEventRecord>(events.Count);

        foreach (var item in events)
        {
            if (ToRecord(item, session, now) is { } record)
            {
                accepted.Add(record);
            }
        }

        if (accepted.Count > 0)
        {
            _events.Publish(accepted);
        }

        return Result<EventsAcceptedDto>.Ok(new EventsAcceptedDto(accepted.Count, events.Count - accepted.Count));
    }

    public async Task<Result<SessionAccessDto>> GetAccessAsync(
        Guid sessionId, CancellationToken cancellationToken = default)
    {
        var access = await _playback.GetAccessAsync(sessionId, cancellationToken);

        if (access is null)
        {
            return Result<SessionAccessDto>.NotFound();
        }

        if (access.StoragePath is null || !access.IsLiveAt(Now()))
        {
            return Result<SessionAccessDto>.Gone(SessionOver);
        }

        return Result<SessionAccessDto>.Ok(new SessionAccessDto(
            sessionId, Utc(access.ExpiresAt).ToUnixTimeSeconds(), access.StoragePath));
    }

    public static string LimitReached(int limit, int active) =>
        $"Your plan allows {limit} stream{(limit == 1 ? string.Empty : "s")} at a time and " +
        $"{active} {(active == 1 ? "is" : "are")} playing. Stop one, or wait a minute for it to time out.";

    private async Task<PlaybackSessionDto> DescribeAsync(
        PlaybackSession session, ContentMedia media, PlaybackPosition? stored, int limit, bool resumed,
        CancellationToken cancellationToken)
    {
        var renditions = await _media.GetRenditionsAsync(session.ContentId, cancellationToken);
        var subtitles = await _media.GetSubtitlesAsync(session.ContentId, cancellationToken);

        var token = MediaToken.Create(
            _key, session.SessionId, Utc(session.StartedAt) + _rules.MaxSessionAge, media.StoragePath!);

        var prefix = "/" + _rules.PublicPrefix.Trim('/');

        string Url(string path) => $"{prefix}/{token}/{path}";

        var progressive = renditions.FirstOrDefault(r => r.Kind == RenditionKinds.Progressive);
        var position = stored?.Position ?? 0f;

        if (media.DurationSeconds is { } duration && position >= duration * ResumeCutoff)
        {
            position = 0f;
        }

        var now = Now();

        return new PlaybackSessionDto(
            session.SessionId,
            session.ContentId,
            Utc(session.ExpiresAt),
            SecondsUntil(session.ExpiresAt, now),
            (int)Math.Max(_rules.Heartbeat.TotalSeconds, 1),
            position,
            media.DurationSeconds,
            media.HlsPath is null ? null : Url(media.HlsPath),
            media.DashPath is null ? null : Url(media.DashPath),
            progressive is null ? null : Url(progressive.Path),
            subtitles.Select(s => new SubtitleTrackDto(s.Language, LanguageTags.For(s.Language), Url(s.Uri))).ToList(),
            renditions
                .Where(r => r.Kind != RenditionKinds.Progressive)
                .OrderBy(r => r.Kind == RenditionKinds.Video ? 0 : 1)
                .ThenByDescending(r => r.Bitrate)
                .Select(MediaService.ToDto)
                .ToList(),
            limit,
            resumed);
    }

    private async Task<PlaybackSession?> OwnedAsync(Guid sessionId, Caller caller, CancellationToken cancellationToken)
    {
        var userId = await _accounts.FindUserIdAsync(caller.UserId!, cancellationToken);

        if (userId is null)
        {
            return null;
        }

        var session = await _playback.FindAsync(sessionId, cancellationToken);

        return session is not null && session.UserId == userId.Value ? session : null;
    }

    private static PlaybackEventRecord? ToRecord(PlaybackEventCommand item, PlaybackSession session, DateTime now)
    {
        if (item.EventId == Guid.Empty || item.Seq < 0 || !PlaybackEventTypes.IsKnown(item.Type))
        {
            return null;
        }

        var position = item.Position is { } reported && PlaybackPosition.IsValid(reported) ? reported : 0f;
        var detail = (item.Detail ?? string.Empty).Trim();

        return new PlaybackEventRecord(
            item.EventId,
            session.DeviceId,
            item.Seq,
            session.SessionId,
            session.ContentId,
            item.Type!,
            PlaybackDeliveries.IsKnown(item.Delivery) ? item.Delivery! : string.Empty,
            OccurredAt(item.At, now),
            position,
            Math.Clamp(item.DurationMs ?? 0, 0, MaxDurationMs),
            Math.Clamp(item.From ?? 0, 0, MaxHeight),
            Math.Clamp(item.To ?? 0, 0, MaxHeight),
            detail.Length <= MaxDetailLength ? detail : detail[..MaxDetailLength]);
    }

    private static DateTime OccurredAt(long? at, DateTime now)
    {
        if (at is not { } milliseconds)
        {
            return now;
        }

        var earliest = new DateTimeOffset(now - MaxClockSkew).ToUnixTimeMilliseconds();
        var latest = new DateTimeOffset(now + MaxClockSkew).ToUnixTimeMilliseconds();

        return milliseconds < earliest || milliseconds > latest
            ? now
            : DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime;
    }

    private DateTime Now() => DateTime.SpecifyKind(_time.GetUtcNow().UtcDateTime, DateTimeKind.Unspecified);

    private static DateTime Earlier(DateTime a, DateTime b) => a <= b ? a : b;

    private static DateTimeOffset Utc(DateTime stored) =>
        new(DateTime.SpecifyKind(stored, DateTimeKind.Utc));

    private static int SecondsUntil(DateTime expiresAt, DateTime now) =>
        (int)Math.Max(Math.Ceiling((expiresAt - now).TotalSeconds), 0);
}
