using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Blog.Api.Analytics
{
    public class PostView
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }

        [BsonElement("postId")]
        public int PostId { get; set; }

        [BsonElement("viewedAt")]
        public DateTime ViewedAt { get; set; }

        [BsonElement("referrer")]
        [BsonIgnoreIfNull]
        public string? Referrer { get; set; }

        [BsonElement("userId")]
        [BsonIgnoreIfNull]
        public string? UserId { get; set; }

        [BsonElement("visitorHash")]
        [BsonIgnoreIfNull]
        public string? VisitorHash { get; set; }
    }

    public class PostViewCount
    {
        public int PostId { get; set; }
        public long Views { get; set; }
        public long UniqueVisitors { get; set; }
    }

    public interface IViewStore
    {
        bool IsConfigured { get; }
        Task RecordAsync(PostView view, CancellationToken cancellationToken = default);
        Task<List<PostViewCount>> TopPostsAsync(int days, int limit, CancellationToken cancellationToken = default);
        Task<PostViewCount?> ForPostAsync(int postId, CancellationToken cancellationToken = default);
    }

    public class MongoViewStore : IViewStore
    {
        private readonly IMongoCollection<PostView>? _views;
        private readonly ILogger<MongoViewStore> _logger;

        public MongoViewStore(IConfiguration configuration, ILogger<MongoViewStore> logger)
        {
            _logger = logger;

            var connectionString = configuration.GetConnectionString("Mongo");

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return;
            }

            try
            {
                var settings = MongoClientSettings.FromConnectionString(connectionString);
                settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
                settings.ConnectTimeout = TimeSpan.FromSeconds(5);

                var database = new MongoClient(settings).GetDatabase(configuration.GetValue("Analytics:MongoDatabase", "blog_analytics"));

                _views = database.GetCollection<PostView>("post_views");

                EnsureIndexes();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Mongo unavailable; analytics disabled.");
                _views = null;
            }
        }

        public bool IsConfigured => _views is not null;

        private void EnsureIndexes()
        {
            if (_views is null)
            {
                return;
            }

            try
            {
                _views.Indexes.CreateOne(new CreateIndexModel<PostView>(Builders<PostView>.IndexKeys.Ascending(v => v.ViewedAt).Ascending(v => v.PostId)));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not create analytics index.");
            }
        }

        public async Task RecordAsync(PostView view, CancellationToken cancellationToken = default)
        {
            if (_views is null)
            {
                return;
            }

            await _views.InsertOneAsync(view, cancellationToken: cancellationToken);
        }

        public async Task<List<PostViewCount>> TopPostsAsync(int days, int limit, CancellationToken cancellationToken = default)
        {
            if (_views is null)
            {
                return new List<PostViewCount>();
            }

            var since = DateTime.UtcNow.AddDays(-days);

            var results = await _views.Aggregate().Match(v => v.ViewedAt >= since).Group(v => v.PostId, g => new { PostId = g.Key, Views = g.Count(), Visitors = g.Select(v => v.VisitorHash).Distinct().Count() })
                .SortByDescending(g => g.Views)
                .Limit(limit)
                .ToListAsync(cancellationToken);

            return results.Select(r => new PostViewCount{ PostId = r.PostId, Views = r.Views, UniqueVisitors = r.Visitors }).ToList();
        }

        public async Task<PostViewCount?> ForPostAsync(int postId, CancellationToken cancellationToken = default)
        {
            if (_views is null)
            {
                return null;
            }

            var results = await _views.Aggregate()
                .Match(v => v.PostId == postId)
                .Group(v => v.PostId, g => new
                {
                    PostId = g.Key,
                    Views = g.Count(),
                    Visitors = g.Select(v => v.VisitorHash).Distinct().Count()
                })
                .ToListAsync(cancellationToken);

            var first = results.FirstOrDefault();

            return first is null
                ? new PostViewCount { PostId = postId, Views = 0, UniqueVisitors = 0 }
                : new PostViewCount
                {
                    PostId = first.PostId,
                    Views = first.Views,
                    UniqueVisitors = first.Visitors
                };
        }
    }
}
