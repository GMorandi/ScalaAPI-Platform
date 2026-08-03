using System.Security.Cryptography;
using System.Text;
using Sub2Api.Grains.Interfaces;

namespace Sub2Api.Host.Services;

public sealed class CredentialProtector : ICredentialProtector
{
    private const string Prefix = "enc:v1:";
    private readonly byte[] _key;

    public CredentialProtector(IConfiguration configuration)
    {
        var encoded = configuration["Security:MasterKey"]
            ?? throw new InvalidOperationException("Security:MasterKey is required");
        try
        {
            _key = Convert.FromBase64String(encoded);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Security:MasterKey must be base64", ex);
        }
        if (_key.Length != 32)
            throw new InvalidOperationException("Security:MasterKey must decode to 32 bytes");
    }

    public string Protect(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var source = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[source.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, tag.Length);
        aes.Encrypt(nonce, source, ciphertext, tag);
        var result = new byte[nonce.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, result, nonce.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, result, nonce.Length + tag.Length, ciphertext.Length);
        return Prefix + Convert.ToBase64String(result);
    }

    public string Unprotect(string protectedValue)
    {
        if (!protectedValue.StartsWith(Prefix, StringComparison.Ordinal))
            return protectedValue;
        var source = Convert.FromBase64String(protectedValue[Prefix.Length..]);
        if (source.Length < 28) throw new CryptographicException("Invalid protected credential");
        var plaintext = new byte[source.Length - 28];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(source.AsSpan(0, 12), source.AsSpan(28),
            source.AsSpan(12, 16), plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }
}
