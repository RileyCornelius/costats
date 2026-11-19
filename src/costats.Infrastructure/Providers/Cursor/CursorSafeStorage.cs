using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace costats.Infrastructure.Providers.Cursor;

/// <summary>
/// Decrypts values Cursor stores through Electron safeStorage, written to state.vscdb as
/// "cursor-safe-storage:v1:&lt;base64&gt;". The base64 blob is Chromium's os_crypt format:
/// "v10" + 12-byte nonce + AES-256-GCM ciphertext + 16-byte tag, keyed by the DPAPI-protected
/// os_crypt.encrypted_key in Cursor's "Local State" file.
/// </summary>
internal static class CursorSafeStorage
{
    private const string Prefix = "cursor-safe-storage:";
    private const string BlobVersion = "v10";
    private const string DpapiKeyPrefix = "DPAPI";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public static bool IsEncrypted(string value) => value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// Decrypts a "cursor-safe-storage:v1:..." value with the given AES key. Returns null when
    /// the value is malformed or the key does not match.
    /// </summary>
    public static string? Decrypt(string value, byte[] key)
    {
        if (!IsEncrypted(value))
        {
            return null;
        }

        // Skip the "vN:" scheme segment after the prefix; the blob carries its own version.
        var separator = value.IndexOf(':', Prefix.Length);
        if (separator < 0)
        {
            return null;
        }

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(value[(separator + 1)..]);
        }
        catch (FormatException)
        {
            return null;
        }

        var headerSize = BlobVersion.Length + NonceSize;
        if (blob.Length < headerSize + TagSize
            || !blob.AsSpan(0, BlobVersion.Length).SequenceEqual(Encoding.ASCII.GetBytes(BlobVersion)))
        {
            return null;
        }

        var nonce = blob.AsSpan(BlobVersion.Length, NonceSize);
        var ciphertext = blob.AsSpan(headerSize, blob.Length - headerSize - TagSize);
        var tag = blob.AsSpan(blob.Length - TagSize, TagSize);
        var plaintext = new byte[ciphertext.Length];

        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch (CryptographicException)
        {
            return null;
        }

        return Encoding.UTF8.GetString(plaintext);
    }

    /// <summary>
    /// Reads and DPAPI-unprotects the os_crypt master key from Cursor's "Local State" file.
    /// Returns null when the file or key is missing, or on non-Windows platforms.
    /// </summary>
    public static byte[]? LoadMasterKey(string localStatePath)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(localStatePath))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(localStatePath));
        if (!doc.RootElement.TryGetProperty("os_crypt", out var osCrypt)
            || !osCrypt.TryGetProperty("encrypted_key", out var encryptedKeyElement)
            || encryptedKeyElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var encryptedKey = Convert.FromBase64String(encryptedKeyElement.GetString()!);
        if (encryptedKey.Length <= DpapiKeyPrefix.Length
            || !encryptedKey.AsSpan(0, DpapiKeyPrefix.Length).SequenceEqual(Encoding.ASCII.GetBytes(DpapiKeyPrefix)))
        {
            return null;
        }

        try
        {
            return ProtectedData.Unprotect(encryptedKey[DpapiKeyPrefix.Length..], null, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
