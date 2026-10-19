using System.Diagnostics;
using System.Text;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;

namespace Caligo.Observability;

public static class KafkaTracing
{
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

    public static PropagationContext Extract(Func<string, string?> getHeader) =>
        Propagators.DefaultTextMapPropagator.Extract(
            default,
            getHeader,
            static (getter, key) =>
            {
                var value = getter(key);

                return value is null ? Array.Empty<string>() : new[] { value };
            });

    public static Activity? StartProduce(string topic, string? key = null)
    {
        var activity = CaligoActivitySources.Kafka.StartActivity(
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

    public static Activity? StartConsume(string topic, string consumerGroup, PropagationContext parent)
    {
        var activity = CaligoActivitySources.Kafka.StartActivity(
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