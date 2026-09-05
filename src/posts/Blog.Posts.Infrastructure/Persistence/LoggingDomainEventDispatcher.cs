using Microsoft.Extensions.Logging;
using Blog.Posts.Application.Abstractions;
using Blog.Posts.Domain.Events;

namespace Blog.Posts.Infrastructure.Persistence
{
    public class LoggingDomainEventDispatcher : IDomainEventDispatcher
    {
        private readonly ILogger<LoggingDomainEventDispatcher> _logger;

        public LoggingDomainEventDispatcher(ILogger<LoggingDomainEventDispatcher> logger)
        {
            _logger = logger;
        }

        public Task DispatchAsync(IReadOnlyList<IDomainEvent> events, CancellationToken cancellationToken = default)
        {
            foreach (var domainEvent in events)
            {
                _logger.LogInformation(
                    "Domain event {EventType} {EventId} raised at {OccurredAt}.",
                    domainEvent.GetType().Name, domainEvent.EventId, domainEvent.OccurredAt);
            }

            return Task.CompletedTask;
        }
    }
}