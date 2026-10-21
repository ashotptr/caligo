namespace Caligo.Catalog.Application.Engine;

public record RadioQueueRequest(string Path, string Artist, string Album, string Title);

public record RadioQueueResult(bool Success, string Message, string? TrackId);

public record AskSong(string Artist, string Title, string? Url);

public record AskAnswer(string? Text, IReadOnlyList<AskSong> Songs);

public record RotationRequest(string Folder, string? File, string? Artist, string? Album, string? Title);

public record CaligoTrack(string Title, string AudioUrl);

public record MixtapeTrack(string Folder, string File, string Title, string Artist, string Album);

public record QueueTrackCommand(int ContentId);

public record RotationCommand(int? AlbumId, int? SingleId, int? ContentId);

public record AskCommand(string Query, string? SessionId);
