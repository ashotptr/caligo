namespace Caligo.Catalog.Application.Collections;

public record CollectionSummaryDto(int CollectionId, string Name, int Titles, int Songs, string? CoverUri);

public record CollectionTitleDto(
    int ContentId,
    string Title,
    string Kind,
    float Duration,
    string? AudioUri,
    List<string> Artists,
    int? AlbumId,
    string? AlbumTitle,
    string? CoverUri,
    int Position);

public record CollectionDetailDto(int CollectionId, string Name, List<CollectionTitleDto> Titles);

public record CollectionNameCommand(string Name);

public record AddTitlesCommand(IReadOnlyList<int> ContentIds);

public record AddTitlesResult(int Added, int AlreadyThere);
