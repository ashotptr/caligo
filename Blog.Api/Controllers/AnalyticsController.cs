using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Blog.Api.Analytics;
using Blog.Api.Data;

namespace Blog.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AnalyticsController : ControllerBase
    {
        private readonly IViewStore _views;
        private readonly ApplicationDbContext _context;

        public AnalyticsController(IViewStore views, ApplicationDbContext context)
        {
            _views = views;
            _context = context;
        }

        [HttpGet("posts/{id}")]
        public async Task<ActionResult<object>> ForPost(int id, CancellationToken ct)
        {
            if (!_views.IsConfigured)
            {
                return Ok(new { postId = id, views = 0L, uniqueVisitors = 0L, enabled = false });
            }

            var count = await _views.ForPostAsync(id, ct);

            return Ok(new
            {
                postId = id,
                views = count?.Views ?? 0,
                uniqueVisitors = count?.UniqueVisitors ?? 0,
                enabled = true
            });
        }

        [HttpGet("top")]
        [Authorize(Roles = "Admin,Writer")]
        public async Task<ActionResult<object>> Top([FromQuery] int days = 7, [FromQuery] int limit = 10, CancellationToken ct = default)
        {
            days = Math.Clamp(days, 1, 365);
            limit = Math.Clamp(limit, 1, 50);

            if (!_views.IsConfigured)
            {
                return Ok(new { enabled = false, days, posts = Array.Empty<object>() });
            }

            var counts = await _views.TopPostsAsync(days, limit, ct);

            if (counts.Count == 0)
            {
                return Ok(new { enabled = true, days, posts = Array.Empty<object>() });
            }

            var ids = counts.Select(c => c.PostId).ToList();

            var titles = await _context.BlogPosts.Where(p => ids.Contains(p.Id)).Select(p => new { p.Id, p.Title }).ToDictionaryAsync(p => p.Id, p => p.Title, ct);

            return Ok(new
            {
                enabled = true,
                days,
                posts = counts.Select(c => new
                {
                    postId = c.PostId,
                    title = titles.TryGetValue(c.PostId, out var title) ? title : "(deleted)",
                    views = c.Views,
                    uniqueVisitors = c.UniqueVisitors
                })
            });
        }
    }
}
