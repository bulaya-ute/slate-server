namespace Slate.Server.Links;

public record BacklinkDto(Guid NoteId, string Path, string Title, string ContextSnippet);

public record GraphNodeDto(Guid Id, string Path, string Title, int LinkCount);

public record GraphEdgeDto(Guid Source, Guid Target);

public record GraphResponse(IReadOnlyList<GraphNodeDto> Nodes, IReadOnlyList<GraphEdgeDto> Edges);
