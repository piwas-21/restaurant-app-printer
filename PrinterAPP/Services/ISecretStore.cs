namespace PrinterAPP.Services;

/// <summary>
/// Minimal secret persistence abstraction so configuration code (and its plain net10.0 unit
/// tests) doesn't depend on MAUI SecureStorage. Implementations must be non-throwing:
/// unavailability is reported via null / false so callers can fall back to the legacy
/// plaintext-file field. Implementations must never log the secret value.
/// </summary>
public interface ISecretStore
{
    /// <summary>The stored value, or null when the secret is missing or the store is unavailable.</summary>
    Task<string?> GetAsync(string name);

    /// <summary>Stores the value; false when the platform store is unavailable or the write failed.</summary>
    Task<bool> TrySetAsync(string name, string value);
}
