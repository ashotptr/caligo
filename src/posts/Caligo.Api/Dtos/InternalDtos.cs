using System.Text.Json.Serialization;

namespace Caligo.Api.Dtos;

public class PackagingClaimRequest
{
    [JsonPropertyName("attempts")]
    public int Attempts { get; set; }

    [JsonPropertyName("source_file")]
    public string? SourceFile { get; set; }
}

public class PackagingFailureRequest : PackagingClaimRequest
{
    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public class PackagedRenditionRequest
{
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("codec")]
    public string? Codec { get; set; }

    [JsonPropertyName("bitrate")]
    public int Bitrate { get; set; }

    [JsonPropertyName("width")]
    public int? Width { get; set; }

    [JsonPropertyName("height")]
    public int? Height { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }
}

public class PackagedSubtitleRequest
{
    [JsonPropertyName("language_id")]
    public int LanguageId { get; set; }

    [JsonPropertyName("uri")]
    public string? Uri { get; set; }
}

public class PackagingReportRequest : PackagingClaimRequest
{
    [JsonPropertyName("storage_path")]
    public string? StoragePath { get; set; }

    [JsonPropertyName("hls_path")]
    public string? HlsPath { get; set; }

    [JsonPropertyName("dash_path")]
    public string? DashPath { get; set; }

    [JsonPropertyName("duration_seconds")]
    public float DurationSeconds { get; set; }

    [JsonPropertyName("renditions")]
    public List<PackagedRenditionRequest>? Renditions { get; set; }

    [JsonPropertyName("subtitles")]
    public List<PackagedSubtitleRequest>? Subtitles { get; set; }
}
