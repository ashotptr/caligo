using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Caligo.Api.Dtos;
using Caligo.Posts.Application.Claps;
using Caligo.Posts.Application.Posts;

namespace Caligo.Api.Controllers;

[Route("api/[controller]")]
[ApiController]
[EnableRateLimiting("reads")]
public class BlogPostsController : ApiControllerBase
{
    private readonly PostService _posts;
    private readonly ClapService _claps;

    public BlogPostsController(PostService posts, ClapService claps)
    {
        _posts = posts;
        _claps = claps;
    }

    [HttpGet]
    public async Task<IActionResult> GetBlogPosts(
        [FromQuery] string? cursor,
        [FromQuery] int limit = PostService.DefaultPageSize,
        [FromQuery] bool mine = false,
        [FromQuery] int? contentId = null,
        CancellationToken cancellationToken = default) =>
        Respond(contentId is { } reviewed
            ? await _posts.GetReviewsAsync(reviewed, cursor, limit, cancellationToken)
            : await _posts.GetPageAsync(cursor, limit, mine, CurrentCaller(), cancellationToken));

    [HttpGet("{id}")]
    public async Task<IActionResult> GetBlogPost(int id, CancellationToken cancellationToken)
    {
        var result = await _posts.GetAsync(id, CurrentView(), CurrentCaller(), cancellationToken);

        if (result.IsSuccess)
        {
            Response.Headers.ETag = ETag(result.Value!.Version);
        }

        return Respond(result);
    }

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

        var command = ToCommand(blogPostDto) with { ExpectedVersion = ExpectedVersion() };

        return Respond(await _posts.UpdateAsync(id, command, CurrentCaller(), cancellationToken));
    }

    [HttpDelete("{id}")]
    [Authorize]
    public async Task<IActionResult> DeleteBlogPost(int id, CancellationToken cancellationToken) =>
        Respond(await _posts.DeleteAsync(id, CurrentCaller(), ExpectedVersion(), cancellationToken));

    [HttpPost("{id}/claps")]
    [Authorize]
    public async Task<IActionResult> Clap(int id, ClapDto clap, CancellationToken cancellationToken) =>
        Respond(await _claps.ClapAsync(id, clap.Count, CurrentCaller(), cancellationToken));

    [HttpGet("{id}/claps")]
    public async Task<IActionResult> GetClaps(int id, CancellationToken cancellationToken) =>
        Respond(await _claps.TallyAsync(id, CurrentCaller(), cancellationToken));

    [HttpGet("claps/audit")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> AuditClaps([FromQuery] int? postId, CancellationToken cancellationToken) =>
        Ok(await _claps.AuditAsync(postId, cancellationToken));

    private static WritePostCommand ToCommand(BlogPostDto dto) =>
        new(dto.Title, dto.Content, dto.Tags, dto.PublishAt?.UtcDateTime, ContentId: dto.ContentId);

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

        var material = $"{address}|{Request.Headers.UserAgent}|{DateTime.UtcNow:yyyy-MM-dd}";

        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material));

        return Convert.ToHexString(bytes)[..16];
    }
}
