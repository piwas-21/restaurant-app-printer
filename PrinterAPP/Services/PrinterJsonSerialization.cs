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
}
