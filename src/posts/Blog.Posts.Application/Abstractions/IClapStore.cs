namespace Blog.Posts.Application.Abstractions;

// Applied is how many of the requested claps counted after the per-reader cap; ReaderTotal is the
// reader's running total on the post.
public record ClapOutcome(int Applied, int ReaderTotal, bool FirstClap);

public record ClapTally(long Total, long Clappers, int Mine);

public record ClapMismatch(int PostId, long Expected, long RolledUp, long Pending);

public record ClapAudit(
    int PostsChecked,
    long ExpectedTotal,
    long RolledUpTotal,
    long PendingTotal,
    IReadOnlyList<ClapMismatch> Mismatches);

public interface IClapStore
{
    // Returns null when the post no longer exists.
    Task<ClapOutcome?> ClapAsync(int postId, string userId, int count, CancellationToken cancellationToken = default);

    Task<ClapTally> TallyAsync(int postId, string? userId, CancellationToken cancellationToken = default);

    Task<ClapAudit> AuditAsync(int? postId, CancellationToken cancellationToken = default);
}
