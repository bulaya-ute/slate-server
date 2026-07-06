namespace Slate.Server.Common;

/// <summary>Single source of truth for password requirements, shared by setup/register/admin-user endpoints.</summary>
public static class PasswordPolicy
{
    public const int MinimumLength = 8;
}
