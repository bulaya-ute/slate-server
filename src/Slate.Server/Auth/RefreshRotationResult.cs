using Slate.Server.Domain;

namespace Slate.Server.Auth;

public enum RefreshRotationOutcome
{
    NotFound,
    Expired,
    Reused,
    UserDisabled,
    Success,
}

/// <summary>Outcome of <see cref="IRefreshTokenService.RotateAsync"/>.</summary>
public class RefreshRotationResult
{
    public required RefreshRotationOutcome Outcome { get; init; }
    public User? User { get; init; }
    public string? NewPlainToken { get; init; }

    public static RefreshRotationResult NotFound() => new() { Outcome = RefreshRotationOutcome.NotFound };
    public static RefreshRotationResult Expired() => new() { Outcome = RefreshRotationOutcome.Expired };
    public static RefreshRotationResult Reused() => new() { Outcome = RefreshRotationOutcome.Reused };
    public static RefreshRotationResult UserDisabled() => new() { Outcome = RefreshRotationOutcome.UserDisabled };

    public static RefreshRotationResult Success(User user, string newPlainToken) => new()
    {
        Outcome = RefreshRotationOutcome.Success,
        User = user,
        NewPlainToken = newPlainToken,
    };
}
