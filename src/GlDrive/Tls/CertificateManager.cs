using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using GlDrive.Config;
using Serilog;

namespace GlDrive.Tls;

public class CertificateManager
{
    private readonly string _fingerprintFile;
    private Dictionary<string, TrustedCert> _trustedCerts = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public CertificateManager(string? fingerprintFileName = null)
    {
        _fingerprintFile = Path.Combine(
            ConfigManager.AppDataPath,
            fingerprintFileName ?? "trusted_certs.json");
        Load();
    }

    public event Func<string, string, Task<bool>>? CertificatePrompt;

    public string GetFingerprint(X509Certificate certificate)
    {
        using var cert2 = new X509Certificate2(certificate);
        var hash = cert2.GetCertHash(HashAlgorithmName.SHA256);
        return Convert.ToHexString(hash);
    }

    public async Task<bool> ValidateCertificate(string host, int port, X509Certificate certificate)
    {
        var fingerprint = GetFingerprint(certificate);
        var key = $"{host}:{port}";

        if (_trustedCerts.TryGetValue(key, out var trusted))
        {
            if (trusted.Fingerprint == fingerprint)
            {
                Log.Debug("Certificate fingerprint matches");
                return true;
            }

            Log.Warning("Certificate fingerprint MISMATCH — expected {Expected}, got {Actual}",
                trusted.Fingerprint, fingerprint);
            return false;
        }

        // TOFU: first time seeing this cert
        Log.Information("New certificate encountered: {Fingerprint}", fingerprint);

        if (CertificatePrompt != null)
        {
            var accepted = await CertificatePrompt(key, fingerprint);
            if (!accepted) return false;
        }

        TrustCertificate(key, fingerprint);
        return true;
    }

    public void TrustCertificate(string hostPort, string fingerprint)
    {
        _trustedCerts[hostPort] = new TrustedCert
        {
            Fingerprint = fingerprint,
            TrustedAt = DateTime.UtcNow
        };
        Save();
    }

    public void ClearTrustedCertificates()
    {
        _trustedCerts.Clear();
        Save();
    }

    public void RemoveTrustedCertificate(string hostPort)
    {
        _trustedCerts.Remove(hostPort);
        Save();
    }

    public IReadOnlyDictionary<string, TrustedCert> GetTrustedCertificates() => _trustedCerts;

    private void Load()
    {
        if (!File.Exists(_fingerprintFile)) return;
        try
        {
            var json = File.ReadAllText(_fingerprintFile);
            _trustedCerts = JsonSerializer.Deserialize<Dictionary<string, TrustedCert>>(json, JsonOptions)
                            ?? new();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load trusted certificates");
            _trustedCerts = new();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_fingerprintFile)!);
        var json = JsonSerializer.Serialize(_trustedCerts, JsonOptions);
        File.WriteAllText(_fingerprintFile, json);
    }
}

public class TrustedCert
{
    public string Fingerprint { get; set; } = "";
    public DateTime TrustedAt { get; set; }
}
