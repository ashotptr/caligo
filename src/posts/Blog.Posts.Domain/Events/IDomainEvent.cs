namespace Blog.Posts.Domain.Events;

/// <summary>
/// Something that has happened to the domain, stated in the past tense. Raised by entities
/// and drained by the persistence layer once the change actually commits, so that an event is
/// never published for a transaction that later rolls back.
/// </summary>
public interface IDomainEvent
{
    Guid EventId { get; }

    DateTime OccurredAt { get; }
}
