namespace TrayTimes.Transit;

/// <summary>Only wholly successful crawls export this versioned, provider-ordered picker metadata.</summary>
public sealed record StopDirectoryMetadata(int Version, IReadOnlyList<DirectoryRouteMetadata> Routes);

public sealed record DirectoryRouteMetadata(TransitRoute Route, IReadOnlyList<DirectoryDirectionMetadata> Directions);

public sealed record DirectoryDirectionMetadata(TransitDirection Direction, IReadOnlyList<TransitStop> Stops);
