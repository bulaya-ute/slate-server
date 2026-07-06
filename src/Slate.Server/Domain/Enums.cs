namespace Slate.Server.Domain;

/// <summary>Application-wide user role.</summary>
public enum UserRole
{
    Admin,
    User
}

/// <summary>Access level a user has on a vault.</summary>
public enum VaultAccessLevel
{
    Owner,
    Edit,
    Read
}

/// <summary>The kind of change recorded by a <see cref="Revision"/>.</summary>
public enum RevisionKind
{
    Create,
    Edit,
    Delete,
    Rename,
    Resolve,
    Attach
}
