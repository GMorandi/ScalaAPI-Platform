using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using ScalaAPI.Admin.Auth;
using ScalaAPI.Admin.Security;
using Xunit;

namespace ScalaAPI.Admin.Tests;

public sealed class SecurityTests
{
    private static byte[] GenerateKey() => RandomNumberGenerator.GetBytes(32);

    private static SecretProtector CreateProtector(string? prevKey = null, string? prevId = null)
    {
        var key = Convert.ToBase64String(GenerateKey());
        var dict = new Dictionary<string, string?>
        {
            ["Security:MasterKey"] = key,
        };
        if (prevKey is not null)
        {
            dict["Security:PreviousMasterKey"] = prevKey;
            if (prevId is not null) dict["Security:PreviousKeyId"] = prevId;
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict!).Build();
        return new SecretProtector(config);
    }

    [Fact]
    public void Protect_And_Unprotect_Roundtrip()
    {
        var protector = CreateProtector();
        var plaintext = "super-secret-value-12345";
        var encrypted = protector.Protect(plaintext);
        Assert.NotEqual(plaintext, encrypted);
        var decrypted = protector.Unprotect(encrypted);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void RotateMasterKey_Rewraps_All_Values_Deterministically()
    {
        var protector = CreateProtector();
        var originalKeyId = protector.CurrentKeyId;

        // Encrypt several values with the original key
        var values = new[] { "secret-alpha", "secret-beta", "secret-gamma" };
        var encrypted = values.Select(v => protector.Protect(v)).ToArray();

        // Rotate to a new key
        var newKey = GenerateKey();
        var newKeyId = protector.RotateMasterKey(newKey);
        Assert.NotEqual(originalKeyId, newKeyId);
        Assert.Equal(newKeyId, protector.CurrentKeyId);

        // Old values can still be decrypted (rotation window)
        for (int i = 0; i < values.Length; i++)
        {
            var decrypted = protector.Unprotect(encrypted[i]);
            Assert.Equal(values[i], decrypted);
        }

        // Rewrap all values to the new key
        var rewrapped = encrypted.Select(e => protector.Rewrap(e)).ToArray();

        // Rewrapped values can be decrypted
        for (int i = 0; i < values.Length; i++)
        {
            var decrypted = protector.Unprotect(rewrapped[i]);
            Assert.Equal(values[i], decrypted);
        }

        // RewrapBatch reports correct count of re-encrypted values
        var count = protector.RewrapBatch(encrypted, out var batchRewrapped);
        Assert.Equal(values.Length, count);
        Assert.Equal(values.Length, batchRewrapped.Count);
    }

    [Fact]
    public void Rotation_Window_Allows_Decrypting_Old_Key_Values()
    {
        var oldKey = GenerateKey();
        var oldKeyB64 = Convert.ToBase64String(oldKey);

        // Create a protector with the old key to encrypt values
        var oldConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:MasterKey"] = oldKeyB64,
            }!).Build();
        var oldProtector = new SecretProtector(oldConfig);
        var encrypted = oldProtector.Protect("old-key-secret");

        // Create a new protector with a new key and the old key as previous
        var newKey = GenerateKey();
        var newConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:MasterKey"] = Convert.ToBase64String(newKey),
                ["Security:PreviousMasterKey"] = oldKeyB64,
            }!).Build();
        var newProtector = new SecretProtector(newConfig);

        // Can decrypt old-key values through the rotation window
        var decrypted = newProtector.Unprotect(encrypted);
        Assert.Equal("old-key-secret", decrypted);
    }

    [Fact]
    public void SecretRedaction_Removes_ApiKeys_From_Error_Messages()
    {
        var redaction = new SecretRedactionService();

        var errorWithApiKey = "Request failed: api_key=sk-live-abc123def456 endpoint=/v1/charge";
        var redacted = redaction.Redact(errorWithApiKey);
        Assert.DoesNotContain("sk-live-abc123def456", redacted);
        Assert.Contains("[api_key:redacted]", redacted);

        var errorWithBearer = "Auth failed: Bearer eyJhbGciOiJSUzI1NiJ9.test.sig";
        redacted = redaction.Redact(errorWithBearer);
        Assert.DoesNotContain("eyJhbGciOiJSUzI1NiJ9", redacted);
        Assert.Contains("[bearer:redacted]", redacted);

        var errorWithPassword = "Login failed: password=hunter2 user=admin";
        redacted = redaction.Redact(errorWithPassword);
        Assert.DoesNotContain("hunter2", redacted);
        Assert.Contains("[password_kv:redacted]", redacted);
    }

    [Fact]
    public void SecretRedaction_Redacts_Json_Sensitive_Fields()
    {
        var redaction = new SecretRedactionService();
        var json = """{"username":"admin","password":"s3cret","api_key":"sk-123","data":{"token":"abc","ok":true}}""";
        var redacted = redaction.RedactJson(json);
        Assert.DoesNotContain("s3cret", redacted);
        Assert.DoesNotContain("sk-123", redacted);
        Assert.DoesNotContain("abc", redacted.Replace("[redacted]", ""));
        Assert.Contains("admin", redacted); // non-sensitive preserved
        Assert.Contains("[redacted]", redacted);
    }

    [Fact]
    public void SecretRedaction_RedactException_Handles_InnerExceptions()
    {
        var redaction = new SecretRedactionService();
        var inner = new InvalidOperationException("password=leaked123");
        var outer = new Exception("api_key=sk-top-secret", inner);
        var redacted = redaction.RedactException(outer);
        Assert.DoesNotContain("sk-top-secret", redacted);
        Assert.DoesNotContain("leaked123", redacted);
        Assert.Contains("[api_key:redacted]", redacted);
        Assert.Contains("[password_kv:redacted]", redacted);
    }

    [Fact]
    public void CertificateTracker_Rejects_Expired_Certificate()
    {
        var redaction = new SecretRedactionService();
        var tracker = new CertificateTracker(redaction);
        var ex = Assert.Throws<CryptographicException>(() =>
            tracker.TrackCertificate("cert-1", "CN=expired.example.com", "CN=CA",
                DateTime.UtcNow.AddDays(-365), DateTime.UtcNow.AddDays(-1)));
        Assert.Contains("expired", ex.Message);
    }

    [Fact]
    public void CertificateTracker_Rejects_NotYetValid_Certificate()
    {
        var redaction = new SecretRedactionService();
        var tracker = new CertificateTracker(redaction);
        var ex = Assert.Throws<CryptographicException>(() =>
            tracker.TrackCertificate("cert-2", "CN=future.example.com", "CN=CA",
                DateTime.UtcNow.AddDays(30), DateTime.UtcNow.AddDays(365)));
        Assert.Contains("not yet valid", ex.Message);
    }

    [Fact]
    public void CertificateTracker_Accepts_Valid_Certificate_And_Reports_Status()
    {
        var redaction = new SecretRedactionService();
        var tracker = new CertificateTracker(redaction);
        tracker.TrackCertificate("cert-3", "CN=valid.example.com", "CN=CA",
            DateTime.UtcNow.AddDays(-30), DateTime.UtcNow.AddDays(365));

        var cert = tracker.GetCertificate("cert-3");
        Assert.NotNull(cert);
        Assert.Equal("valid", cert.Status);
        Assert.Null(tracker.ValidateCertificate("cert-3"));
    }

    [Fact]
    public void CertificateTracker_Alerts_On_Expiring_Soon()
    {
        var redaction = new SecretRedactionService();
        var tracker = new CertificateTracker(redaction);
        tracker.TrackCertificate("cert-4", "CN=expiring.example.com", "CN=CA",
            DateTime.UtcNow.AddDays(-350), DateTime.UtcNow.AddDays(15));

        var cert = tracker.GetCertificate("cert-4");
        Assert.NotNull(cert);
        Assert.Equal("expiring_soon", cert.Status);

        var alerts = tracker.RefreshAndAlert();
        Assert.Contains(alerts, a => a.CertId == "cert-4");
    }

    [Fact]
    public void CertificateTracker_Does_Not_Leak_Secrets_In_Error()
    {
        var redaction = new SecretRedactionService();
        var tracker = new CertificateTracker(redaction);
        var ex = Assert.Throws<CryptographicException>(() =>
            tracker.TrackCertificate("cert-secret", "CN=api_key=sk-leaked", "CN=CA",
                DateTime.UtcNow.AddDays(-365), DateTime.UtcNow.AddDays(-1)));
        // The error message should have the secret redacted
        Assert.DoesNotContain("sk-leaked", ex.Message);
    }
}
