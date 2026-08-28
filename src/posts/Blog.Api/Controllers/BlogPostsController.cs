using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;
using Blog.Api.Dtos;
using Blog.Posts.Application.Common;
using Blog.Posts.Application.Posts;

namespace Blog.Api.Controllers;

/// <summary>
/// Translates HTTP to <see cref="PostService"/> calls and back. It holds no rules of its own:
/// caching, ownership and paging all moved into the application layer, where they can be
/// tested without a web server.
/// </summary>
[Route("api/[controller]")]
[ApiController]
[EnableRateLimiting("reads")]
public class BlogPostsController : ControllerBase
{
    private readonly PostService _posts;

    public BlogPostsController(PostService posts)
    {
        _posts = posts;
    }

    [HttpGet]
    public async Task<IActionResult> GetBlogPosts(
        [FromQuery] string? cursor,
        [FromQuery] int limit = PostService.DefaultPageSize,
        [FromQuery] bool mine = false,
        CancellationToken cancellationToken = default) =>
        Respond(await _posts.GetPageAsync(cursor, limit, mine, CurrentCaller(), cancellationToken));

    [HttpGet("{id}")]
    public async Task<IActionResult> GetBlogPost(int id, CancellationToken cancellationToken) =>
        Respond(await _posts.GetAsync(id, CurrentView(), CurrentCaller(), cancellationToken));

    [HttpGet("search")]
    public async Task<IActionResult> SearchBlogPosts(
        [FromQuery] string query,
        [FromQuery] int limit = PostService.DefaultPageSize,
        CancellationToken cancellationToken = default) =>
        Respond(await _posts.SearchAsync(query, limit, cancellationToken));

    [HttpPost]
    [Authorize(Roles = "Admin,Writer")]
    public async Task<IActionResult> PostBlogPost(BlogPostDto blogPostDto, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var result = await _posts.CreateAsync(ToCommand(blogPostDto), CurrentCaller(), cancellationToken);

        return result.IsSuccess
            ? CreatedAtAction(nameof(GetBlogPost), new { id = result.Value!.Id }, result.Value)
            : Respond(result);
    }

    [HttpPut("{id}")]
    [Authorize]
    public async Task<IActionResult> PutBlogPost(int id, BlogPostDto blogPostDto, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        return Respond(await _posts.UpdateAsync(id, ToCommand(blogPostDto), CurrentCaller(), cancellationToken));
    }

    [HttpDelete("{id}")]
    [Authorize]
    public async Task<IActionResult> DeleteBlogPost(int id, CancellationToken cancellationToken) =>
        Respond(await _posts.DeleteAsync(id, CurrentCaller(), cancellationToken));

    private static WritePostCommand ToCommand(BlogPostDto dto) => new(dto.Title, dto.Content, dto.Tags);

    private Caller CurrentCaller() =>
        new(User.FindFirstValue(ClaimTypes.NameIdentifier), User.IsInRole("Admin"));

    private ViewContext CurrentView() => new(
        Request.Headers.Referer.ToString() is { Length: > 0 } referrer ? referrer : null,
        VisitorHash());

    private string? VisitorHash()
    {
        var address = HttpContext.Connection.RemoteIpAddress?.ToString();

        if (string.IsNullOrEmpty(address))
        {
            return null;
        }

        // Rotated daily so a visitor can be counted once per day without the raw IP being stored.
        var material = $"{address}|{Request.Headers.UserAgent}|{DateTime.UtcNow:yyyy-MM-dd}";

        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material));

        return Convert.ToHexString(bytes)[..16];
    }

    private IActionResult Respond<T>(Result<T> result) => result.Status switch
    {
        ResultStatus.Ok => Ok(result.Value),
        ResultStatus.NotFound => NotFound(),
        ResultStatus.Forbidden => Forbid(),
        ResultStatus.Unauthorized => Unauthorized(),
        ResultStatus.Invalid => BadRequest(result.Error),
        _ => StatusCode(StatusCodes.Status500InternalServerError)
    };

    private IActionResult Respond(Result result) => result.Status switch
    {
        ResultStatus.Ok => NoContent(),
        ResultStatus.NotFound => NotFound(),
        ResultStatus.Forbidden => Forbid(),
        ResultStatus.Unauthorized => Unauthorized(),
        ResultStatus.Invalid => BadRequest(result.Error),
        _ => StatusCode(StatusCodes.Status500InternalServerError)
    };
}
