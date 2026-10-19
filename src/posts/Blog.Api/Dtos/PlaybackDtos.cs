using System.Text.Json.Serialization;

namespace Blog.Api.Dtos;

public class StartSessionRequest
{
    [JsonPropertyName("content_id")]
    public int? ContentId { get; set; }

    [JsonPropertyName("device_id")]
    public string? DeviceId { get; set; }
}

public class PositionRequest
{
    [JsonPropertyName("position")]
    public float? Position { get; set; }

    [JsonPropertyName("seq")]
    public long? Seq { get; set; }
}

public class CompleteRequest
{
    [JsonPropertyName("completion_id")]
    public Guid? CompletionId { get; set; }

    [JsonPropertyName("position")]
    public float? Position { get; set; }
}

public class PlaybackEventRequest
{
    [JsonPropertyName("event_id")]
    public Guid EventId { get; set; }

    [JsonPropertyName("seq")]
    public long Seq { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("at")]
    public long? At { get; set; }

    [JsonPropertyName("position")]
    public float? Position { get; set; }

    [JsonPropertyName("duration_ms")]
    public int? DurationMs { get; set; }

    [JsonPropertyName("from")]
    public int? From { get; set; }

    [JsonPropertyName("to")]
    public int? To { get; set; }

    [JsonPropertyName("delivery")]
    public string? Delivery { get; set; }

    [JsonPropertyName("detail")]
    public string? Detail { get; set; }
}

public class PlaybackEventsRequest
{
    [JsonPropertyName("events")]
    public List<PlaybackEventRequest>? Events { get; set; }
}

public class MediaRequest
{
    public string? SourceFile { get; set; }
}
