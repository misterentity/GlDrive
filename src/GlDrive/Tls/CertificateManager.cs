using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
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

            // Certificate changed — possible MitM. Prompt user or reject.
            Log.Warning("Certificate fingerprint changed for {Key} — old: {Old}, new: {New}",
                key, trusted.Fingerprint[..16] + "...", fingerprint[..16] + "...");

            if (CertificatePrompt != null)
            {
                var accepted = await CertificatePrompt(key,
                    $"⚠ Certificate changed for {key}!\n\n" +
                    $"Old fingerprint: {trusted.Fingerprint[..16]}...\n" +
                    $"New fingerprint: {fingerprint[..16]}...\n\n" +
                    "This could indicate a man-in-the-middle attack.\n" +
                    "Accept the new certificate?");
                if (accepted)
                {
                    TrustCertificate(key, fingerprint);
                    return true;
                }
            }

            Log.Error("Certificate change rejected for {Key} — fingerprint mismatch", key);
            return false;
        }

        // TOFU: first time seeing this cert — auto-trust
        Log.Information("Auto-trusting new certificate for {Key}: {Fingerprint}", key, fingerprint[..16] + "...");
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
        var tempPath = _fingerprintFile + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _fingerprintFile, overwrite: true);
        RestrictFilePermissions(_fingerprintFile);
    }

    private static void RestrictFilePermissions(string filePath)
    {
        try
        {
            var fileInfo = new System.IO.FileInfo(filePath);
            var security = fileInfo.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            // Remove all existing rules
            var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
            foreach (FileSystemAccessRule rule in rules)
                security.RemoveAccessRule(rule);
            // Grant full control only to current user
            var currentUser = WindowsIdentity.GetCurrent().User!;
            security.AddAccessRule(new FileSystemAccessRule(
                currentUser, FileSystemRights.FullControl, AccessControlType.Allow));
            fileInfo.SetAccessControl(security);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to restrict file permissions on {File}", filePath);
        }
    }
}

public class TrustedCert
{
    public string Fingerprint { get; set; } = "";
    public DateTime TrustedAt { get; set; }
}
