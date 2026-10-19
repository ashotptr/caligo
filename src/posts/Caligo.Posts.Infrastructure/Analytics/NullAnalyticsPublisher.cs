using Caligo.Posts.Application.Abstractions;

namespace Caligo.Posts.Infrastructure.Analytics
{
    public class NullAnalyticsPublisher : IAnalyticsPublisher
    {
        public void PublishPostViewed(PostViewedEvent viewed)
        {
        }

        public void PublishPostClapped(PostClappedEvent clapped)
        {
        }
    }
}
