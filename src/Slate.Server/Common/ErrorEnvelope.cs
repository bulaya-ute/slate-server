namespace Slate.Server.Common;

/// <summary>The `{code, message}` half of the API's error envelope.</summary>
public record ErrorBody(string Code, string Message);

/// <summary>
/// Wire shape for every error response: `{"error": {"code": "...", "message": "..."}}`.
/// Used both for errors written explicitly by controllers and for the generic fallback in
/// <see cref="Program"/>'s status-code-pages handler (e.g. 401/403 raised by the auth middleware
/// before any controller action runs).
/// </summary>
public record ErrorEnvelope(ErrorBody Error)
{
    public ErrorEnvelope(string code, string message) : this(new ErrorBody(code, message))
    {
    }
}
