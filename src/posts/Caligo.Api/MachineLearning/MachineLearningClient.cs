using System.Net.Http.Json;
using System.Text.Json;
using Caligo.Catalog.Application.Contents;

namespace Caligo.Api.MachineLearning;

public class MachineLearningClient
{
    public static readonly TimeSpan CommentBudget = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan AdminBudget = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions SnakeCase = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly HttpClient _http;
    private readonly ILogger<MachineLearningClient> _logger;

    public MachineLearningClient(HttpClient http, ILogger<MachineLearningClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public bool IsConfigured => _http.BaseAddress is not null;

    public async Task ScoreCommentAsync(CommentDto comment, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(CommentBudget);

        try
        {
            using var response = await _http.PostAsJsonAsync(
                "moderation/comments",
                new CommentScore(comment.CommentId, comment.ContentId, comment.Comment),
                SnakeCase,
                budget.Token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "Comment {CommentId} was not scored ({Status}); the next moderation run will score it",
                    comment.CommentId, (int)response.StatusCode);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            _logger.LogInformation(
                "Comment {CommentId} was not scored ({Reason}); the next moderation run will score it",
                comment.CommentId, exception.GetType().Name);
        }
    }

    public async Task<(int Status, string Body)> SendAsync(
        HttpMethod method, string path, object? body, CancellationToken cancellationToken = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(AdminBudget);

        using var request = new HttpRequestMessage(method, path)
        {
            Content = body is null ? null : JsonContent.Create(body, options: SnakeCase)
        };
        using var response = await _http.SendAsync(request, budget.Token);

        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(budget.Token));
    }

    private sealed record CommentScore(int CommentId, int ContentId, string Text);
}
