using Microsoft.Maui.Storage;

namespace PrinterAPP.Services;

/// <summary>
/// <see cref="ISecretStore"/> backed by MAUI SecureStorage (Android Keystore / Windows DPAPI).
///
/// KNOWN PLATFORM LIMIT: on Windows this app ships as an UNPACKAGED .exe
/// (WindowsPackageType=None) and SecureStorage requires package identity there, so every call
/// throws. The catch blocks below turn that into the documented fallback — the API key keeps
/// living in config.json, exactly the pre-migration status quo — while Android gets the secure
/// path. The broad catches are deliberate: any platform failure here must degrade to the
/// fallback, never crash startup or the polling loop on the live client machine.
///
/// Never log the secret value handled here.
/// </summary>
public class SecureStorageSecretStore : ISecretStore
{
    public async Task<string?> GetAsync(string name)
    {
        try
        {
            return await SecureStorage.Default.GetAsync(name);
        }
        catch (Exception)
        {
            return null; // store unavailable on this platform — caller falls back to the file field
        }
    }

    public async Task<bool> TrySetAsync(string name, string value)
    {
        try
        {
            await SecureStorage.Default.SetAsync(name, value);
            return true;
        }
        catch (Exception)
        {
            return false; // store unavailable — caller keeps the plaintext-file behaviour
        }
    }
}
