using System.Diagnostics;
using System.Text;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;

namespace Blog.Observability;

/// <summary>
/// Carries trace context across Kafka.
///
/// This is what a distributed trace needs that HTTP gets for free. Within a request, the trace
/// id rides on the wire because every HTTP client injects it; across a broker there is no
/// client to do that, so the producer has to write the W3C traceparent into the message headers
/// itself and the consumer has to read it back out. Without it a trace stops dead at the
/// publish, and the consumer's work appears as an unrelated trace minutes later - which is
/// exactly the case where knowing what caused what matters most.
/// </summary>
public static class KafkaTracing
{
    /// <summary>
    /// Writes the current trace context into message headers, returning the traceparent so a
    /// caller that stores the message for later - an outbox row - can keep it.
    /// </summary>
    public static string? Inject(Action<string, string> setHeader, Activity? activity = null)
    {
        activity ??= Activity.Current;

        if (activity is null)
        {
            return null;
        }

        Propagators.DefaultTextMapPropagator.Inject(
            new PropagationContext(activity.Context, Baggage.Current),
            setHeader,
            static (setter, key, value) => setter(key, value));

        return activity.Id;
    }

    /// <summary>
    /// Rebuilds the context a message was produced under, so the consumer's span attaches to
    /// the trace that caused it rather than starting a new one.
    /// </summary>
    public static PropagationContext Extract(Func<string, string?> getHeader) =>
        Propagators.DefaultTextMapPropagator.Extract(
            default,
            getHeader,
            static (getter, key) =>
            {
                var value = getter(key);

                return value is null ? Array.Empty<string>() : new[] { value };
            });

    /// <summary>
    /// Starts a producer span. Named and tagged to the OpenTelemetry messaging conventions, so
    /// a backend recognises it as a publish rather than as an anonymous internal span.
    /// </summary>
    public static Activity? StartProduce(string topic, string? key = null)
    {
        var activity = BlogActivitySources.Kafka.StartActivity(
            $"{topic} publish", ActivityKind.Producer);

        if (activity is null)
        {
            return null;
        }

        activity.SetTag("messaging.system", "kafka");
        activity.SetTag("messaging.operation", "publish");
        activity.SetTag("messaging.destination.name", topic);

        if (key is not null)
        {
            activity.SetTag("messaging.kafka.message.key", key);
        }

        return activity;
    }

    /// <summary>
    /// Starts a consumer span parented to whatever produced the message, however long ago.
    /// </summary>
    public static Activity? StartConsume(string topic, string consumerGroup, PropagationContext parent)
    {
        var activity = BlogActivitySources.Kafka.StartActivity(
            $"{topic} receive", ActivityKind.Consumer, parent.ActivityContext);

        if (activity is null)
        {
            return null;
        }

        activity.SetTag("messaging.system", "kafka");
        activity.SetTag("messaging.operation", "receive");
        activity.SetTag("messaging.destination.name", topic);
        activity.SetTag("messaging.consumer.group.name", consumerGroup);

        return activity;
    }

    /// <summary>
    /// Rebuilds a context from a stored traceparent string.
    ///
    /// Needed by the outbox relay: the request that wrote the row finished long before the
    /// relay published it, so the only surviving link to that trace is the traceparent saved on
    /// the row. Restoring it here is what stops "post created" and "email sent" from looking
    /// like unrelated events.
    /// </summary>
    public static PropagationContext FromTraceParent(string? traceParent)
    {
        if (string.IsNullOrWhiteSpace(traceParent))
        {
            return default;
        }

        return Propagators.DefaultTextMapPropagator.Extract(
            default,
            traceParent,
            static (value, key) => key == "traceparent" ? new[] { value } : Array.Empty<string>());
    }

    public static string? HeaderValue(IEnumerable<KeyValuePair<string, byte[]>> headers, string key)
    {
        foreach (var header in headers)
        {
            if (string.Equals(header.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return Encoding.UTF8.GetString(header.Value);
            }
        }

        return null;
    }
}
