namespace Slate.Server.Notes;

/// <summary>No-op <see cref="IRevisionBroadcaster"/> registered until S5 adds the real SignalR hub.</summary>
public sealed class NullRevisionBroadcaster : IRevisionBroadcaster
{
    public Task BroadcastAsync(RevisionBroadcast revision, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
