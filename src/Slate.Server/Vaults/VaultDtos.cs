namespace Slate.Server.Vaults;

/// <summary>Wire shape for a vault as seen by one particular member (their own access role + usage stats).</summary>
public record VaultDto(Guid Id, string Name, string Role, int NoteCount, long SizeBytes, DateTimeOffset CreatedAt);

public record CreateVaultRequest(string? Name);

public record RenameVaultRequest(string? Name);
