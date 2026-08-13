using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ScalaAPI.Admin.Security;

/// <summary>
/// Tracks TLS certificates and rejects expired or not-yet-valid ones.
/// Provides background health checking and upcoming-expiry alerts.
/// </summary>
public sealed class CertificateTracker
{
    private readonly Dictionary<string, CertificateRecord> _certs = new();
    private readonly Lock _lock = new();
    private readonly SecretRedactionService _redaction;

    public CertificateTracker(SecretRedactionService redaction)
    {
        _redaction = redaction;
    }

    public record CertificateRecord(
        string CertId,
        string Subject,
        string? Issuer,
        DateTime NotBefore,
        DateTime NotAfter,
        DateTime? LastCheckedAt,
        string Status);

    /// <summary>
    /// Register or update a certificate. Throws if the certificate is expired or not yet valid.
    /// </summary>
    public void TrackCertificate(string certId, string subject, string? issuer,
        DateTime notBefore, DateTime notAfter, DateTime? lastChecked = null)
    {
        var now = DateTime.UtcNow;
        if (notAfter <= now)
            throw new CryptographicException(
                _redaction.Redact($"Certificate '{certId}' (subject={subject}) is expired: notAfter={notAfter:O}"));
        if (notBefore > now)
            throw new CryptographicException(
                _redaction.Redact($"Certificate '{certId}' (subject={subject}) is not yet valid: notBefore={notBefore:O}"));

        var status = (notAfter - now).TotalDays switch
        {
            < 7 => "expiring_critical",
            < 30 => "expiring_soon",
            _ => "valid",
        };

        lock (_lock)
        {
            _certs[certId] = new CertificateRecord(certId, subject, issuer,
                notBefore, notAfter, lastChecked ?? now, status);
        }
    }

    /// <summary>
    /// Track from an X509Certificate2 instance.
    /// </summary>
    public void TrackCertificate(X509Certificate2 cert)
    {
        TrackCertificate(
            cert.Thumbprint ?? cert.Subject,
            cert.Subject,
            cert.Issuer,
            cert.NotBefore.ToUniversalTime(),
            cert.NotAfter.ToUniversalTime());
    }

    public IReadOnlyList<CertificateRecord> ListCertificates()
    {
        lock (_lock)
        {
            return _certs.Values.ToList().AsReadOnly();
        }
    }

    public CertificateRecord? GetCertificate(string certId)
    {
        lock (_lock)
        {
            return _certs.GetValueOrDefault(certId);
        }
    }

    /// <summary>
    /// Validate that a certificate is currently valid (not expired, not future-dated).
    /// Returns null if valid, or an error message if not.
    /// </summary>
    public string? ValidateCertificate(string certId)
    {
        CertificateRecord? record;
        lock (_lock)
        {
            record = _certs.GetValueOrDefault(certId);
        }
        if (record is null) return "certificate_not_found";
        var now = DateTime.UtcNow;
        if (record.NotAfter <= now) return "certificate_expired";
        if (record.NotBefore > now) return "certificate_not_yet_valid";
        return null;
    }

    /// <summary>
    /// Refresh status of all tracked certificates based on current time.
    /// Returns certificates that are expiring soon (within 30 days).
    /// </summary>
    public IReadOnlyList<CertificateRecord> RefreshAndAlert()
    {
        var alerts = new List<CertificateRecord>();
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var keys = _certs.Keys.ToList();
            foreach (var key in keys)
            {
                var old = _certs[key];
                var status = (old.NotAfter - now).TotalDays switch
                {
                    <= 0 => "expired",
                    < 7 => "expiring_critical",
                    < 30 => "expiring_soon",
                    _ => "valid",
                };
                var updated = old with { LastCheckedAt = now, Status = status };
                _certs[key] = updated;
                if (status is "expired" or "expiring_critical" or "expiring_soon")
                    alerts.Add(updated);
            }
        }
        return alerts.AsReadOnly();
    }
}
