using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrinterAPP.Services;

/// <summary>One read contract for the backend's camelCase payloads and named enum values.</summary>
internal static class PrinterJsonSerialization
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    internal static bool AreEquivalent<T>(T first, T second) =>
        string.Equals(
            JsonSerializer.Serialize(first, Options),
            JsonSerializer.Serialize(second, Options),
            StringComparison.Ordinal);
}
