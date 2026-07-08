namespace Slate.Server.Notes;

/// <summary>
/// Wire shape broadcast to SignalR group "vault:{vaultId}" as event "revision" (design spec Sync
/// protocol "Live feed") - the same shape as a `rev` entry from <c>GET /vaults/{v}/changes</c>, plus
/// the vault id.
/// </summary>
public record RevisionBroadcast(
    Guid VaultId,
    Guid? NoteId,
    long Seq,
    string Kind,
    string Path,
    string? OldPath,
    string ContentHash,
    string DeviceId,
    bool IsConflict,
    DateTimeOffset CreatedAt);

/// <summary>
/// Notified once a mutating note/attachment operation has committed its revision row, so live
/// clients can be told about it. S4 appends the revision and calls this for every such operation but
/// only wires up <see cref="NullRevisionBroadcaster"/> (no-op); S5 replaces the DI registration with
/// a SignalR-hub-backed implementation - callers (NoteService, AttachmentsController) don't change.
/// </summary>
public interface IRevisionBroadcaster
{
    Task BroadcastAsync(RevisionBroadcast revision, CancellationToken cancellationToken = default);
}
