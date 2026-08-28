using Blog.Posts.Application.Abstractions;

namespace Blog.Posts.Infrastructure.Analytics
{
    /// <summary>
    /// Used when no broker is configured, so the API runs standalone and the tests do not need
    /// Kafka. Analytics is the only thing lost.
    /// </summary>
    public class NullAnalyticsPublisher : IAnalyticsPublisher
    {
        public void PublishPostViewed(PostViewedEvent viewed)
        {
        }
    }
}
