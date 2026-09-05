using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Blog.Contracts;
using Blog.Contracts.Events;
using Blog.Posts.Application.Abstractions;
using Blog.Posts.Domain.Events;

namespace Blog.Posts.Infrastructure.Persistence
{
    public class OutboxDomainEventDispatcher : IDomainEventDispatcher
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        private readonly ApplicationDbContext _context;
        private readonly ILogger<OutboxDomainEventDispatcher> _logger;

        public OutboxDomainEventDispatcher(ApplicationDbContext context, ILogger<OutboxDomainEventDispatcher> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task DispatchAsync(IReadOnlyList<IDomainEvent> events, CancellationToken cancellationToken = default)
        {
            if (events.Count == 0)
            {
                return;
            }

            var traceParent = Activity.Current?.Id;

            foreach (var domainEvent in events)
            {
                var integrationEvent = Translate(domainEvent);

                if (integrationEvent is null)
                {
                    _logger.LogDebug("Domain event {EventType} is internal; not published.", domainEvent.GetType().Name);

                    continue;
                }

                _context.OutboxMessages.Add(new OutboxMessage
                {
                    Id = integrationEvent.EventId,
                    EventType = integrationEvent.EventType,
                    Topic = Topics.PostLifecycle,
                    PartitionKey = integrationEvent.PartitionKey,
                    Payload = JsonSerializer.Serialize(integrationEvent, integrationEvent.GetType(), Json),
                    SchemaVersion = integrationEvent.SchemaVersion,
                    OccurredAt = integrationEvent.OccurredAt,
                    TraceParent = traceParent
                });
            }

            await _context.SaveChangesAsync(cancellationToken);
        }

        private static IntegrationEvent? Translate(IDomainEvent domainEvent) => domainEvent switch
        {
            PostPublished published => new PostPublishedEvent
            {
                EventId = published.EventId,
                OccurredAt = published.OccurredAt,
                PostId = published.PostId,
                Title = published.Title,
                AuthorId = published.AuthorId,
                Tags = published.Tags
            },

            PostUpdated updated => new PostUpdatedEvent
            {
                EventId = updated.EventId,
                OccurredAt = updated.OccurredAt,
                PostId = updated.PostId,
                Title = updated.Title
            },

            PostDeleted deleted => new PostDeletedEvent
            {
                EventId = deleted.EventId,
                OccurredAt = deleted.OccurredAt,
                PostId = deleted.PostId
            },

            _ => null
        };
    }
}
