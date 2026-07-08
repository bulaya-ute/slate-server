namespace Slate.Server.Vaults;

public record TreeResponse(IReadOnlyList<string> Folders, IReadOnlyList<NoteSummaryDto> Notes);

public record NoteSummaryDto(Guid Id, string Path, string Title, bool HasConflict, long SizeBytes, DateTimeOffset UpdatedAt);

public record CreateFolderRequest(string? Path);

public record RenameFolderRequest(string? Path, string? NewPath);
