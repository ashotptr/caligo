using Blog.Catalog.Application.Media;
using Blog.Catalog.Domain.Media;

namespace Blog.Catalog.Application.Abstractions;

public record SubtitleTrack(int SubtitleId, string Language, string Uri, string Format);

public interface IMediaStore
{
    Task<ContentMedia?> FindAsync(int contentId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Rendition>> GetRenditionsAsync(int contentId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SubtitleTrack>> GetSubtitlesAsync(int contentId, CancellationToken cancellationToken = default);

    Task<ContentMedia?> QueueAsync(
        int contentId, string sourceFile, DateTime now, CancellationToken cancellationToken = default);
}

public record InboxEntry(string File, long Size, DateTime Modified);

public interface IMediaInbox
{
    bool IsConfigured { get; }

    IReadOnlyList<InboxEntry> List();

    bool Contains(string fileName);
}

public record OpenSession(
    int UserId,
    int ContentId,
    string DeviceId,
    DateTime Now,
    DateTime ExpiresAt,
    int MaxStreams,
    DateTime ReuseStartedAfter,
    DateTime PurgeBefore);

public record OpenedSession(PlaybackSession? Session, bool Resumed, int ActiveStreams);

public record MediaAccess(DateTime ExpiresAt, DateTime? EndedAt, string? StoragePath)
{
    public bool IsLiveAt(DateTime now) => EndedAt is null && ExpiresAt > now;
}

public record SavedPosition(float Position, long Seq, DateTime ExpiresAt);

public enum CompletionOutcome
{
    Completed,
    Repeated,
    Missing,
    Mismatch
}

public record Completion(CompletionOutcome Outcome, PlaybackSession? Session);

public interface IPlaybackStore
{
    Task<OpenedSession> OpenAsync(OpenSession request, CancellationToken cancellationToken = default);

    Task<PlaybackSession?> FindAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task<MediaAccess?> GetAccessAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task<PlaybackPosition?> GetPositionAsync(int userId, int contentId, CancellationToken cancellationToken = default);

    Task<SavedPosition?> SavePositionAsync(
        PlaybackSession session, float position, long seq, DateTime now, DateTime expiresAt,
        CancellationToken cancellationToken = default);

    Task<Completion> CompleteAsync(
        Guid sessionId, int userId, Guid completionId, float? position, DateTime now,
        CancellationToken cancellationToken = default);

    Task<bool> EndAsync(Guid sessionId, int userId, DateTime now, CancellationToken cancellationToken = default);
}

public interface IPlaybackEventSink
{
    void Publish(IReadOnlyList<PlaybackEventRecord> events);
}
