using Microsoft.Extensions.Options;
using Blog.Notifications.Core;

namespace Blog.Notifications.Host.Messaging;

public record FanOutResult(int Claimed, int Queued);

// Turns one post.published event into one email command per active subscriber. A popular author
// publishing is the spike: the audience is claimed chunk by chunk, each chunk a single set-based
// insert, so memory and transaction size stay flat however long the list grows, and replaying the
// event creates nothing twice.
public class NewsletterFanOut
{
    private readonly IDeliveryLedger _ledger;
    private readonly ICommandQueue _queue;
    private readonly NotificationsOptions _options;

    public NewsletterFanOut(IDeliveryLedger ledger, ICommandQueue queue, IOptions<NotificationsOptions> options)
    {
        _ledger = ledger;
        _queue = queue;
        _options = options.Value;
    }

    public async Task<FanOutResult> RunAsync(
        Guid eventId, int postId, string postTitle, CancellationToken cancellationToken = default)
    {
        var claimed = 0;
        var queued = 0;
        var after = Guid.Empty;

        while (true)
        {
            var chunk = await _ledger.ClaimChunkAsync(
                eventId, postId, postTitle, after, _options.FanOutChunkSize, cancellationToken);

            var published = new List<Guid>(chunk.Claimed.Count);

            foreach (var command in chunk.Claimed)
            {
                if (await _queue.EnqueueAsync(command, cancellationToken))
                {
                    published.Add(command.DeliveryId);
                }
            }

            // Only what actually reached the broker becomes Queued; the rest stays Pending and the
            // sweeper sends it once the broker is back.
            await _ledger.MarkQueuedAsync(published, cancellationToken);

            claimed += chunk.Claimed.Count;
            queued += published.Count;

            if (chunk.Next is not { } next)
            {
                return new FanOutResult(claimed, queued);
            }

            after = next;
        }
    }
}
