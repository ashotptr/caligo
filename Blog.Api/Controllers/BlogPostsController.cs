using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using System.Security.Claims;
using System.Text.Json;
using Blog.Api.Analytics;
using Blog.Api.Data;
using Blog.Api.Dtos;
using Blog.Api.Models;

[Route("api/[controller]")]
[ApiController]
public class BlogPostsController : ControllerBase
{
    private const string AllPostsCacheKey = "BlogPosts_All";

    private readonly ApplicationDbContext _context;
    private readonly IDistributedCache _cache;
    private readonly ILogger<BlogPostsController> _logger;
    private readonly IAnalyticsPublisher _analytics;

    public BlogPostsController(ApplicationDbContext context, IDistributedCache cache, ILogger<BlogPostsController> logger, IAnalyticsPublisher analytics)
    {
        _context = context;
        _cache = cache;
        _logger = logger;
        _analytics = analytics;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<PostSummaryDto>>> GetBlogPosts()
    {
        var cached = await TryCacheGetAsync(AllPostsCacheKey);

        if (cached != null)
        {
            var cachedPosts = JsonSerializer.Deserialize<List<PostSummaryDto>>(cached);

            if (cachedPosts != null)
            {
                return Ok(cachedPosts);
            }
        }

        var posts = await _context.BlogPosts
            .Include(p => p.Author)
            .OrderByDescending(p => p.PublishedDate)
            .ToListAsync();

        var summaries = posts.Select(ToSummary).ToList();

        await TryCacheSetAsync(AllPostsCacheKey, JsonSerializer.Serialize(summaries));

        return Ok(summaries);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<PostDetailDto>> GetBlogPost(int id)
    {
        var blogPost = await _context.BlogPosts.Include(p => p.Author).FirstOrDefaultAsync(p => p.Id == id);

        if (blogPost == null)
        {
            return NotFound();
        }

        _analytics.PublishPostViewed(new PostViewedEvent
        {
            PostId = blogPost.Id,
            ViewedAt = DateTime.UtcNow,
            Referrer = Request.Headers.Referer.ToString() is { Length: > 0 } referrer ? referrer : null,
            UserId = User.FindFirstValue(ClaimTypes.NameIdentifier),
            VisitorHash = VisitorHash()
        });

        return Ok(ToDetail(blogPost));
    }

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

    [HttpPost]
    [Authorize(Roles = "Admin,Writer")]
    public async Task<ActionResult<PostDetailDto>> PostBlogPost(BlogPostDto blogPostDto)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        var newBlogPost = new BlogPost
        {
            Title = blogPostDto.Title,
            Content = blogPostDto.Content,
            Tags = NormalizeTags(blogPostDto.Tags),
            AuthorId = userId,
            PublishedDate = DateTime.UtcNow
        };

        _context.BlogPosts.Add(newBlogPost);

        await _context.SaveChangesAsync();
        
        await TryCacheRemoveAsync(AllPostsCacheKey);

        await _context.Entry(newBlogPost).Reference(p => p.Author).LoadAsync();

        return CreatedAtAction(nameof(GetBlogPost), new { id = newBlogPost.Id }, ToDetail(newBlogPost));
    }

    [HttpPut("{id}")]
    [Authorize]
    public async Task<IActionResult> PutBlogPost(int id, BlogPostDto blogPostDto)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var postToUpdate = await _context.BlogPosts.FindAsync(id);

        if (postToUpdate == null)
        {
            return NotFound();
        }

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        
        if (postToUpdate.AuthorId != userId && !User.IsInRole("Admin"))
        {
            return Forbid();
        }

        postToUpdate.Title = blogPostDto.Title;
        postToUpdate.Content = blogPostDto.Content;
        postToUpdate.Tags = NormalizeTags(blogPostDto.Tags);

        await _context.SaveChangesAsync();
        
        await TryCacheRemoveAsync(AllPostsCacheKey);

        return NoContent();
    }

    [HttpDelete("{id}")]
    [Authorize]
    public async Task<IActionResult> DeleteBlogPost(int id)
    {
        var postToDelete = await _context.BlogPosts.FindAsync(id);

        if (postToDelete == null)
        {
            return NotFound();
        }

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        
        if (postToDelete.AuthorId != userId && !User.IsInRole("Admin"))
        {
            return Forbid();
        }

        _context.BlogPosts.Remove(postToDelete);

        await _context.SaveChangesAsync();
        
        await TryCacheRemoveAsync(AllPostsCacheKey);

        return NoContent();
    }

    [HttpGet("search")]
    public async Task<ActionResult<IEnumerable<PostSummaryDto>>> SearchBlogPosts([FromQuery] string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return BadRequest("Search query cannot be empty.");
        }

        var lowerCaseQuery = query.ToLower();

        var results = await _context.BlogPosts
            .Include(p => p.Author)
            .Where(p =>
                p.Title.ToLower().Contains(lowerCaseQuery) ||
                p.Content.ToLower().Contains(lowerCaseQuery) ||
                p.Tags.Any(t => t.ToLower().Contains(lowerCaseQuery))
            )
            .OrderByDescending(p => p.PublishedDate)
            .ToListAsync();

        return Ok(results.Select(ToSummary));
    }

    private static PostSummaryDto ToSummary(BlogPost post) => new()
    {
        Id = post.Id,
        Title = post.Title,
        Excerpt = BuildExcerpt(post.Content),
        PublishedDate = post.PublishedDate,
        AuthorName = post.Author?.UserName ?? "Unknown",
        Tags = post.Tags
    };

    private static PostDetailDto ToDetail(BlogPost post) => new()
    {
        Id = post.Id,
        Title = post.Title,
        Content = post.Content,
        PublishedDate = post.PublishedDate,
        AuthorName = post.Author?.UserName ?? "Unknown",
        AuthorId = post.AuthorId,
        Tags = post.Tags
    };

    private static string BuildExcerpt(string content)
    {
        const int maxLength = 240;

        var lines = content.Split('\n');
        var plainLines = new List<string>();
        bool inCodeFence = false;
        
        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("```"))
            {
                inCodeFence = !inCodeFence;
        
                continue;
            }
            if (!inCodeFence)
            {
                plainLines.Add(line.Replace("**", string.Empty).Replace("*", string.Empty).Replace("`", string.Empty).TrimStart('#', ' '));
            }
        }

        var plain = string.Join(" ", plainLines.Where(l => !string.IsNullOrWhiteSpace(l))).Trim();

        return plain.Length <= maxLength ? plain : plain[..maxLength].TrimEnd() + "…";
    }

    private static List<string> NormalizeTags(List<string>? tags)
    {
        if (tags == null)
        {
            return new List<string>();
        }

        return tags
            .Select(t => t.Trim().ToLowerInvariant())
            .Where(t => t.Length > 0)
            .Distinct()
            .Take(10)
            .ToList();
    }

    private async Task<string?> TryCacheGetAsync(string key)
    {
        try
        {
            return await _cache.GetStringAsync(key);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache read failed for {CacheKey}", key);

            return null;
        }
    }

    private async Task TryCacheSetAsync(string key, string value)
    {
        try
        {
            await _cache.SetStringAsync(key, value, new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache write failed for {CacheKey}", key);
        }
    }

    private async Task TryCacheRemoveAsync(string key)
    {
        try
        {
            await _cache.RemoveAsync(key);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache invalidation failed for {CacheKey}", key);
        }
    }
}
