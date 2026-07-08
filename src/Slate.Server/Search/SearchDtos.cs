namespace Slate.Server.Search;

public record SearchResultDto(Guid NoteId, string Path, string Title, string SnippetHtml, double Score);
