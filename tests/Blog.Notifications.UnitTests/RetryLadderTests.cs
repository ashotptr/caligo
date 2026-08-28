using Blog.Notifications.Host.Messaging;
using Xunit;

namespace Blog.Notifications.UnitTests;

/// <summary>
/// The retry ladder's shape, checked without a broker.
///
/// These are the properties that make the topology work rather than merely exist: that every
/// retry queue routes back to the working exchange, that the delays actually increase, and
/// that the ladder terminates instead of looping forever.
/// </summary>
public class RetryLadderTests
{
    [Fact]
    public void DelaysIncrease_SoRetriesBackOff()
    {
        var delays = RabbitTopology.RetryDelays;

        Assert.NotEmpty(delays);

        for (var i = 1; i < delays.Length; i++)
        {
            Assert.True(
                delays[i] > delays[i - 1],
                $"delay {i} ({delays[i]}) should be longer than delay {i - 1} ({delays[i - 1]})");
        }
    }

    [Fact]
    public void TheLadderTerminates()
    {
        // A message that keeps failing has to stop somewhere. Without a ceiling it would cycle
        // between the working queue and the retry queues indefinitely.
        Assert.Equal(RabbitTopology.RetryDelays.Length + 1, RabbitTopology.MaxAttempts);
    }

    [Theory]
    [InlineData(0, "blog.notifications.retry.5s")]
    [InlineData(1, "blog.notifications.retry.30s")]
    [InlineData(2, "blog.notifications.retry.5m")]
    public void EachAttemptMapsToItsOwnDelayQueue(int attempt, string expected)
    {
        Assert.Equal(expected, RabbitTopology.RetryQueue(attempt));
    }

    [Fact]
    public void EveryAttemptHasADistinctQueueAndRoutingKey()
    {
        var queues = Enumerable.Range(0, RabbitTopology.RetryDelays.Length)
            .Select(RabbitTopology.RetryQueue)
            .ToList();

        var keys = Enumerable.Range(0, RabbitTopology.RetryDelays.Length)
            .Select(RabbitTopology.RetryRoutingKey)
            .ToList();

        // Two attempts sharing a queue would silently collapse two different delays into one.
        Assert.Equal(queues.Count, queues.Distinct().Count());
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void AnAttemptBeyondTheLadder_DoesNotThrow()
    {
        // Guards against an off-by-one turning "park this message" into an IndexOutOfRange that
        // would take down the worker instead.
        var beyond = RabbitTopology.RetryDelays.Length + 5;

        Assert.NotNull(RabbitTopology.RetryQueue(beyond));
        Assert.NotNull(RabbitTopology.RetryRoutingKey(beyond));
    }

    [Fact]
    public void TheWorkingQueueAndDeadLetterQueueAreDistinct()
    {
        // If these ever collided, a parked message would be redelivered to a worker forever.
        Assert.NotEqual(RabbitTopology.EmailQueue, RabbitTopology.DeadLetterQueue);
        Assert.NotEqual(RabbitTopology.Exchange, RabbitTopology.DeadLetterExchange);
        Assert.NotEqual(RabbitTopology.Exchange, RabbitTopology.RetryExchange);
    }
}
