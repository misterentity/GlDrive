using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Util;
using Serilog;

namespace GlDrive.Tls;

public class CertificateManager
{
    private readonly string _fingerprintFile;
    private readonly StoreState _state;
    private static readonly ConcurrentDictionary<string, StoreState> Stores =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed class StoreState
    {
        public Lock Sync { get; } = new();
        public SemaphoreSlim ValidationGate { get; } = new(1, 1);
        public Dictionary<string, TrustedCert> TrustedCerts { get; set; } = new();
        public bool Loaded { get; set; }
    }

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
        _fingerprintFile = Path.GetFullPath(_fingerprintFile);
        _state = Stores.GetOrAdd(_fingerprintFile, _ => new StoreState());
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
        await _state.ValidationGate.WaitAsync();
        try
        {
            TrustedCert? trusted;
            lock (_state.Sync)
                _state.TrustedCerts.TryGetValue(key, out trusted);

            if (trusted != null)
            {
                if (trusted.Fingerprint == fingerprint)
                {
                    Log.Debug("Certificate fingerprint matches");
                    return true;
                }

                // Certificate changed — possible MitM. Prompt user or reject.
                Log.Warning("Certificate fingerprint changed for {Key} — old: {Old}, new: {New}",
                    key, trusted.Fingerprint[..Math.Min(16, trusted.Fingerprint.Length)] + "...",
                    fingerprint[..Math.Min(16, fingerprint.Length)] + "...");

                if (CertificatePrompt != null)
                {
                    var accepted = await CertificatePrompt(key,
                        $"⚠ Certificate changed for {key}!\n\n" +
                        $"Old fingerprint: {trusted.Fingerprint[..Math.Min(16, trusted.Fingerprint.Length)]}...\n" +
                        $"New fingerprint: {fingerprint[..Math.Min(16, fingerprint.Length)]}...\n\n" +
                        "This could indicate a man-in-the-middle attack.\n" +
                        "Accept the new certificate?");
                    if (accepted)
                    {
                        TrustCertificate(key, fingerprint, certificate);
                        return true;
                    }
                }

                Log.Error("Certificate change rejected for {Key} — fingerprint mismatch", key);
                return false;
            }

            // TOFU: first time seeing this cert — auto-trust
            Log.Information("Auto-trusting new certificate for {Key}: {Fingerprint}", key, fingerprint[..16] + "...");
            TrustCertificate(key, fingerprint, certificate);
            return true;
        }
        finally
        {
            _state.ValidationGate.Release();
        }
    }

    public void TrustCertificate(string hostPort, string fingerprint)
    {
        TrustCertificate(hostPort, fingerprint, certificate: null);
    }

    public void TrustCertificate(string hostPort, string fingerprint, X509Certificate? certificate)
    {
        string? subject = null;
        string? issuer = null;
        DateTime? notAfter = null;

        if (certificate != null)
        {
            try
            {
                using var cert2 = certificate as X509Certificate2 ?? new X509Certificate2(certificate);
                subject = cert2.Subject;
                issuer = cert2.Issuer;
                notAfter = cert2.NotAfter;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to extract X.509 metadata for {HostPort}", hostPort);
            }
        }

        lock (_state.Sync)
        {
            _state.TrustedCerts[hostPort] = new TrustedCert
            {
                Fingerprint = fingerprint,
                TrustedAt = DateTime.UtcNow,
                Subject = subject,
                Issuer = issuer,
                NotAfter = notAfter
            };
            SaveLocked();
        }
    }

    public void ClearTrustedCertificates()
    {
        lock (_state.Sync)
        {
            _state.TrustedCerts.Clear();
            SaveLocked();
        }
    }

    public void RemoveTrustedCertificate(string hostPort)
    {
        lock (_state.Sync)
        {
            _state.TrustedCerts.Remove(hostPort);
            SaveLocked();
        }
    }

    public IReadOnlyDictionary<string, TrustedCert> GetTrustedCertificates()
    {
        lock (_state.Sync)
            return new Dictionary<string, TrustedCert>(_state.TrustedCerts);
    }

    private void Load()
    {
        lock (_state.Sync)
        {
            if (_state.Loaded) return;
            try
            {
                if (File.Exists(_fingerprintFile))
                {
                    var json = File.ReadAllText(_fingerprintFile);
                    _state.TrustedCerts = JsonSerializer.Deserialize<Dictionary<string, TrustedCert>>(json, JsonOptions)
                                          ?? new();
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load trusted certificates");
                _state.TrustedCerts = new();
            }
            _state.Loaded = true;
        }
    }

    private void SaveLocked()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_fingerprintFile)!);
        var json = JsonSerializer.Serialize(_state.TrustedCerts, JsonOptions);
        SecureFile.WriteAllTextRestricted(_fingerprintFile, json);
    }
}

public class TrustedCert
{
    public string Fingerprint { get; set; } = "";
    public DateTime TrustedAt { get; set; }
    public string? Subject { get; set; }
    public string? Issuer { get; set; }
    public DateTime? NotAfter { get; set; }
}
