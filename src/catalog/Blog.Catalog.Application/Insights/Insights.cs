using Blog.SharedKernel;

namespace Blog.Catalog.Application.Insights;

public record PopularContentDto(int ContentId, string Title, long TotalViews);

public record EngagingContentDto(
    int ContentId, string Title, long TotalViews, long TotalInteractions, long CommentsLeft, decimal? EngagementRatio);

public record ActiveUserDto(int UserId, string? FullName, long TotalViews, long TotalInteractions, long CommentsLeft);

public record SavedQueryDto(int Number, string Title);

public record QueryResultDto(int Number, string Title, List<string> Columns, List<List<object?>> Rows);

public interface IInsights
{
    Task<IReadOnlyList<PopularContentDto>> GetPopularAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EngagingContentDto>> GetEngagingAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ActiveUserDto>> GetActiveUsersAsync(CancellationToken cancellationToken = default);

    IReadOnlyList<SavedQueryDto> Queries { get; }

    Task<QueryResultDto?> RunQueryAsync(int number, CancellationToken cancellationToken = default);
}

public class InsightsService
{
    private readonly IInsights _insights;

    public InsightsService(IInsights insights)
    {
        _insights = insights;
    }

    public Task<IReadOnlyList<PopularContentDto>> GetPopularAsync(CancellationToken cancellationToken = default) =>
        _insights.GetPopularAsync(cancellationToken);

    public Task<IReadOnlyList<EngagingContentDto>> GetEngagingAsync(CancellationToken cancellationToken = default) =>
        _insights.GetEngagingAsync(cancellationToken);

    public Task<IReadOnlyList<ActiveUserDto>> GetActiveUsersAsync(CancellationToken cancellationToken = default) =>
        _insights.GetActiveUsersAsync(cancellationToken);

    public IReadOnlyList<SavedQueryDto> Queries => _insights.Queries;

    public async Task<Result<QueryResultDto>> RunQueryAsync(int number, CancellationToken cancellationToken = default)
    {
        var result = await _insights.RunQueryAsync(number, cancellationToken);

        return result is null ? Result<QueryResultDto>.NotFound() : Result<QueryResultDto>.Ok(result);
    }
}
