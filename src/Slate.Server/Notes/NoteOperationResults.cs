namespace Slate.Server.Notes;

/// <summary>An error a controller should translate directly into the `{error:{code,message}}` envelope.</summary>
public record NoteWriteError(int StatusCode, string ErrorCode, string Message);

/// <summary>Outcome of Create/Rename: either the resulting note metadata, or an error to surface as-is.</summary>
public sealed class NoteOperationResult
{
    public NoteMetaDto? Note { get; private init; }
    public NoteWriteError? Error { get; private init; }

    public static NoteOperationResult Ok(NoteMetaDto note) => new() { Note = note };

    public static NoteOperationResult Fail(int statusCode, string errorCode, string message) =>
        new() { Error = new NoteWriteError(statusCode, errorCode, message) };
}

/// <summary>
/// Outcome of UpdateContent: exactly one of a fast-path success, a sync-protocol conflict, or an
/// error. Modeled as one class (rather than an inheritance hierarchy) since the controller just
/// needs to switch on <see cref="IsConflict"/>/<see cref="Error"/> and pick the matching fields.
/// </summary>
public sealed class UpdateContentOutcome
{
    public bool IsConflict { get; private init; }
    public long RevId { get; private init; }
    public string? ContentHash { get; private init; }
    public long? HeadRevId { get; private init; }
    public NoteWriteError? Error { get; private init; }

    public static UpdateContentOutcome Success(long revId, string contentHash) =>
        new() { RevId = revId, ContentHash = contentHash };

    public static UpdateContentOutcome Conflict(long headRevId, long conflictRevId) =>
        new() { IsConflict = true, HeadRevId = headRevId, RevId = conflictRevId };

    public static UpdateContentOutcome Fail(int statusCode, string errorCode, string message) =>
        new() { Error = new NoteWriteError(statusCode, errorCode, message) };
}
