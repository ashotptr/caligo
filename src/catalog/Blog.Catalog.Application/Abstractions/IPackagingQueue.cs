namespace Blog.Catalog.Application.Abstractions;

public record PackagingJob(int ContentId, string SourceFile, int Attempts, string? StoragePath);

public record PackagedRendition(
    string Kind, string Codec, int Bitrate, int? Width, int? Height, string? Language, string Path);

public record PackagedSubtitle(int LanguageId, string Uri);

public record PackagingResult(
    int ContentId,
    string StoragePath,
    string? HlsPath,
    string? DashPath,
    float DurationSeconds,
    IReadOnlyList<PackagedRendition> Renditions,
    IReadOnlyList<PackagedSubtitle> Subtitles);

public interface IPackagingQueue
{
    Task<PackagingJob?> ClaimAsync(
        DateTime now, DateTime claimedUntil, int maxAttempts, CancellationToken cancellationToken = default);

    Task<bool> ExtendAsync(PackagingJob job, DateTime claimedUntil, CancellationToken cancellationToken = default);

    Task<bool> PublishAsync(
        PackagingJob job, PackagingResult result, DateTime now, CancellationToken cancellationToken = default);

    Task<bool> FailAsync(PackagingJob job, string error, CancellationToken cancellationToken = default);

    Task<bool> ReleaseAsync(PackagingJob job, CancellationToken cancellationToken = default);
}
