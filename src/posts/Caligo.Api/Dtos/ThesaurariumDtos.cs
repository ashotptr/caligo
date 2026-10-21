namespace Caligo.Api.Dtos;

public record CollectionNameRequest(string Name);

public record AddTitlesRequest(List<int> ContentIds);

public record IngestRequest(string Query);

public record QueueTrackRequest(int ContentId);

public record AskRequest(string Query, string? SessionId);

public record RotationJobRequest(int? AlbumId, int? SingleId, int? ContentId);
