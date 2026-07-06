using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Slate.Server.Data.Configurations;

/// <summary>
/// Persists enums as lower-case strings (e.g. UserRole.Admin -> "admin") so DB values and
/// wire values (the JSON API contract, e.g. revision "kind": "create|edit|...") match exactly.
/// </summary>
internal static class EnumConversions
{
    public static ValueConverter<TEnum, string> ForEnum<TEnum>() where TEnum : struct, Enum
        => new(
            v => v.ToString().ToLowerInvariant(),
            v => Enum.Parse<TEnum>(v, true));
}
