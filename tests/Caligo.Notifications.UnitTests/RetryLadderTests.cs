using Caligo.Notifications.Host.Messaging;
using Xunit;

namespace Caligo.Notifications.UnitTests;

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
        Assert.Equal(RabbitTopology.RetryDelays.Length + 1, RabbitTopology.MaxAttempts);
    }

    [Theory]
    [InlineData(0, "caligo.notifications.retry.5s")]
    [InlineData(1, "caligo.notifications.retry.30s")]
    [InlineData(2, "caligo.notifications.retry.5m")]
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

        Assert.Equal(queues.Count, queues.Distinct().Count());
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void AnAttemptBeyondTheLadder_DoesNotThrow()
    {
        var beyond = RabbitTopology.RetryDelays.Length + 5;

        Assert.NotNull(RabbitTopology.RetryQueue(beyond));
        Assert.NotNull(RabbitTopology.RetryRoutingKey(beyond));
    }

    [Fact]
    public void TheWorkingQueueAndDeadLetterQueueAreDistinct()
    {
        Assert.NotEqual(RabbitTopology.EmailQueue, RabbitTopology.DeadLetterQueue);
        Assert.NotEqual(RabbitTopology.Exchange, RabbitTopology.DeadLetterExchange);
        Assert.NotEqual(RabbitTopology.Exchange, RabbitTopology.RetryExchange);
    }
}
