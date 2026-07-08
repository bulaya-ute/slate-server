namespace Slate.Server.Notes;

/// <summary>Note metadata as returned by create/rename/get-meta. `headRevId` lets the client seed its next `baseRevId`.</summary>
public record NoteMetaDto(Guid Id, string Path, string Title, bool HasConflict, long SizeBytes, long? HeadRevId, DateTimeOffset UpdatedAt);

public record CreateNoteRequest(string? Path, string? Content);

public record RenameNoteRequest(string? NewPath);

/// <summary>Body of `PUT /api/notes/{id}/content` - unlike folder ops, deviceId travels in the body per the sync-protocol contract.</summary>
public record UpdateNoteContentRequest(string? Content, long? BaseRevId, string? DeviceId);

/// <summary>200 success shape for `PUT /api/notes/{id}/content`.</summary>
public record UpdateContentSuccessDto(long RevId, string ContentHash);

/// <summary>409 conflict shape for `PUT /api/notes/{id}/content`.</summary>
public record UpdateContentConflictDto(long HeadRevId, long ConflictRevId);
