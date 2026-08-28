using Blog.Posts.Domain.Events;

namespace Blog.Posts.Application.Abstractions;

/// <summary>
/// Hands domain events raised during a change set to whatever is interested in them, after
/// that change set has committed.
///
/// This is the seam the transactional outbox plugs into: today the events are recorded, later
/// the implementation writes them to an outbox table inside the same transaction as the post
/// so that publishing to a broker cannot succeed for a write that rolled back.
/// </summary>
public interface IDomainEventDispatcher
{
    Task DispatchAsync(IReadOnlyList<IDomainEvent> events, CancellationToken cancellationToken = default);
}
