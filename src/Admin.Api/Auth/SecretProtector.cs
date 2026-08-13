using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace ScalaAPI.Admin.Auth;

public sealed class SecretProtector
{
    private volatile KeyHolder _current;
    private readonly ConcurrentDictionary<string, byte[]> _oldKeys = new();
    private readonly Lock _rotationLock = new();
    private string _currentKeyId;

    private sealed record KeyHolder(string KeyId, byte[] Key);

    public SecretProtector(IConfiguration configuration)
    {
        var encoded = configuration["Security:MasterKey"]
            ?? throw new InvalidOperationException("Security:MasterKey is required");
        var key = DecodeKey(encoded);
        _currentKeyId = configuration["Security:KeyId"] ?? ComputeKeyId(key);
        _current = new KeyHolder(_currentKeyId, key);

        // Load previous key if provided (enables rotation window)
        var prevEncoded = configuration["Security:PreviousMasterKey"];
        if (!string.IsNullOrWhiteSpace(prevEncoded))
        {
            var prevKey = DecodeKey(prevEncoded);
            var prevId = configuration["Security:PreviousKeyId"] ?? ComputeKeyId(prevKey);
            _oldKeys[prevId] = prevKey;
        }
    }

    public string CurrentKeyId => _currentKeyId;

    private static byte[] DecodeKey(string encoded)
    {
        try
        {
            var key = Convert.FromBase64String(encoded);
            if (key.Length != 32)
                throw new InvalidOperationException("Security:MasterKey must decode to 32 bytes");
            return key;
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Security:MasterKey must be base64", ex);
        }
    }

    private static string ComputeKeyId(byte[] key)
    {
        var hash = SHA256.HashData(key);
        return Convert.ToHexString(hash.AsSpan(0, 4)); // 4 bytes = 8 hex chars
    }

    public string Protect(string plaintext)
    {
        var holder = _current;
        var nonce = RandomNumberGenerator.GetBytes(12);
        var source = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[source.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(holder.Key, tag.Length);
        aes.Encrypt(nonce, source, ciphertext, tag);
        // Format: keyId(8) + nonce(12) + tag(16) + ciphertext
        var keyIdBytes = System.Text.Encoding.ASCII.GetBytes(holder.KeyId);
        var result = new byte[8 + nonce.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(keyIdBytes, 0, result, 0, 8);
        Buffer.BlockCopy(nonce, 0, result, 8, nonce.Length);
        Buffer.BlockCopy(tag, 0, result, 8 + nonce.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, result, 8 + nonce.Length + tag.Length, ciphertext.Length);
        return Convert.ToBase64String(result);
    }

    public string Unprotect(string protectedValue)
    {
        var source = Convert.FromBase64String(protectedValue);
        if (source.Length < 36) throw new CryptographicException("Invalid protected secret");

        var keyId = System.Text.Encoding.ASCII.GetString(source, 0, 8);

        // Try current key first
        if (keyId == _current.KeyId)
        {
            return Decrypt(source, _current.Key);
        }

        // Try old keys (rotation window)
        if (_oldKeys.TryGetValue(keyId, out var oldKey))
        {
            return Decrypt(source, oldKey);
        }

        throw new CryptographicException($"Unknown key id: {keyId}");
    }

    private static string Decrypt(byte[] source, byte[] key)
    {
        var plaintext = new byte[source.Length - 36];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(source.AsSpan(8, 12), source.AsSpan(8 + 12 + 16), source.AsSpan(8 + 12, 16), plaintext);
        return System.Text.Encoding.UTF8.GetString(plaintext);
    }

    /// <summary>
    /// Rotate the master key. The new key becomes current; the old key is retained
    /// for decryption of existing values (rotation window). Returns the new key id.
    /// </summary>
    public string RotateMasterKey(byte[] newKey)
    {
        if (newKey.Length != 32) throw new ArgumentException("Key must be 32 bytes");

        lock (_rotationLock)
        {
            var oldHolder = _current;
            _oldKeys[oldHolder.KeyId] = oldHolder.Key;
            var newId = ComputeKeyId(newKey);
            _current = new KeyHolder(newId, newKey);
            _currentKeyId = newId;
            return newId;
        }
    }

    /// <summary>
    /// Rewrap a value encrypted with the old key to the current key.
    /// Returns the re-encrypted value, or the original if already on current key.
    /// </summary>
    public string Rewrap(string protectedValue)
    {
        var plaintext = Unprotect(protectedValue);
        return Protect(plaintext);
    }

    /// <summary>
    /// Rewrap a batch of values. Returns the count of values that were actually re-encrypted.
    /// </summary>
    public int RewrapBatch(IEnumerable<string> protectedValues, out List<string> rewrapped)
    {
        rewrapped = new List<string>();
        var count = 0;
        foreach (var val in protectedValues)
        {
            var source = Convert.FromBase64String(val);
            var keyId = System.Text.Encoding.ASCII.GetString(source, 0, 8);
            if (keyId != _current.KeyId)
            {
                rewrapped.Add(Rewrap(val));
                count++;
            }
            else
            {
                rewrapped.Add(val);
            }
        }
        return count;
    }
}
