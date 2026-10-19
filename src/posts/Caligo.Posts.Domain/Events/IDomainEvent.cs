namespace Caligo.Posts.Domain.Events;

public interface IDomainEvent
{
    Guid EventId { get; }

    DateTime OccurredAt { get; }
}
