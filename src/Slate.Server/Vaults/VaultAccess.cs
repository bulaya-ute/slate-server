using Slate.Server.Domain;

namespace Slate.Server.Vaults;

/// <summary>
/// Ranks <see cref="VaultAccessLevel"/> by permissiveness. The enum's declaration order
/// (Owner, Edit, Read) does not match permission order, so callers must go through
/// <see cref="Satisfies"/> rather than comparing the enum's underlying ordinal values directly.
/// </summary>
public static class VaultAccess
{
    private static readonly Dictionary<VaultAccessLevel, int> Rank = new()
    {
        [VaultAccessLevel.Read] = 1,
        [VaultAccessLevel.Edit] = 2,
        [VaultAccessLevel.Owner] = 3,
    };

    /// <summary>True if <paramref name="granted"/> meets or exceeds <paramref name="required"/>.</summary>
    public static bool Satisfies(VaultAccessLevel granted, VaultAccessLevel required) =>
        Rank[granted] >= Rank[required];
}
