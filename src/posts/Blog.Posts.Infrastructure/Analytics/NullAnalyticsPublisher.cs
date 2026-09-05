using Blog.Posts.Application.Abstractions;

namespace Blog.Posts.Infrastructure.Analytics
{
    public class NullAnalyticsPublisher : IAnalyticsPublisher
    {
        public void PublishPostViewed(PostViewedEvent viewed)
        {
        }
    }
}
