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
    private static readonly ConcurrentDictionary<string, StoreState> Stores = new(StringComparer.OrdinalIgnoreCase);
    private sealed class StoreState
    {
        public Lock Sync { get; } = new();
        public Dictionary<string, TrustedCert> TrustedCerts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, PendingTrustDecision> Pending { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, HashSet<string>> IdentityEndpoints { get; } = new(StringComparer.Ordinal);
        public bool UnknownPriorEndpoints { get; set; }
        public bool QuarantineFailed { get; set; }
        public event Action<PendingTrustDecision>? DecisionRaised;
        public bool Loaded { get; set; }
        public bool WriteWarned { get; set; }
        public void Notify(PendingTrustDecision decision)
        {
            // A subscriber (including the UI) can never hold up a TLS handshake or
            // inherit the request/lease attribution of the connection which found it.
            ThreadPool.UnsafeQueueUserWorkItem(_ =>
            {
                foreach (var handler in DecisionRaised?.GetInvocationList() ?? [])
                    try { ((Action<PendingTrustDecision>)handler)(decision); }
                    catch (Exception ex) { Log.Warning(ex, "Certificate decision notification failed"); }
            }, (object?)null);
        }
    }
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public CertificateManager(string? fingerprintFileName = null)
    {
        _fingerprintFile = Path.GetFullPath(Path.Combine(ConfigManager.AppDataPath, fingerprintFileName ?? "trusted_certs.json"));
        _state = Stores.GetOrAdd(_fingerprintFile, _ => new StoreState());
        Load();
    }
    public event Action<PendingTrustDecision> DecisionRaised
    {
        add => _state.DecisionRaised += value;
        remove => _state.DecisionRaised -= value;
    }
    public string GetFingerprint(X509Certificate certificate)
    {
        using var copy = new X509Certificate2(certificate);
        return Convert.ToHexString(copy.GetCertHash(HashAlgorithmName.SHA256));
    }
    public Task<bool> ValidateCertificate(string host, int port, X509Certificate certificate, string? identity = null)
    {
        var incoming = Describe(GetFingerprint(certificate), certificate);
        var key = $"{host}:{port}";
        PendingTrustDecision? raised = null;
        lock (_state.Sync)
        {
            RegisterIdentityLocked(identity, key);
            if (IdentityPendingLocked(identity, key)) return Task.FromResult(false);
            if (!_state.TrustedCerts.ContainsKey(key) && _state.UnknownPriorEndpoints)
                _state.TrustedCerts[key] = new() { RequiresLocalReview = true };
            if (_state.TrustedCerts.TryGetValue(key, out var trusted))
            {
                // Once a change has been observed, no reconnect (even one offering
                // the old pin) resolves the explicit decision on the user's behalf.
                if (!trusted.RequiresLocalReview && !trusted.DecisionRequired && !_state.Pending.ContainsKey(key) && trusted.Fingerprint == incoming.Fingerprint)
                    return Task.FromResult(true);
                if (!_state.Pending.TryGetValue(key, out var pending) || pending.NewFingerprint != incoming.Fingerprint)
                {
                    // A concurrent handshake with the original certificate is not
                    // a new replacement candidate; retain the displayed changed pin.
                    if (trusted.RequiresLocalReview || (trusted.DecisionRequired && !_state.Pending.ContainsKey(key)) || trusted.Fingerprint != incoming.Fingerprint)
                    {
                        raised = new(Guid.NewGuid().ToString("N"), key, trusted.Fingerprint, incoming.Fingerprint,
                            incoming.Subject, incoming.Issuer, incoming.NotAfter, DateTime.UtcNow, false,
                            trusted.RequiresLocalReview ? "The saved certificate store was unreadable and quarantined. This endpoint may have had a pin; verify the presented fingerprint independently before accepting." : null);
                        _state.Pending[key] = raised;
                        if (!trusted.DecisionRequired)
                        {
                            trusted.DecisionRequired = true;
                            SaveLocked();
                        }
                    }
                }
            }
            else
            {
                // Preserve existing TOFU semantics and host:port pin format.
                _state.TrustedCerts[key] = incoming;
                incoming.Identities = IdentitiesForLocked(key);
                SaveLocked();
                Log.Information("Auto-trusting first-use TLS certificate");
                return Task.FromResult(true);
            }
        }
        if (raised != null)
        {
            Log.Warning("TLS certificate changed; endpoint parked for local review");
            _state.Notify(raised);
        }
        return Task.FromResult(false);
    }
    public bool IsPending(string host, int port, string? identity = null)
    {
        lock (_state.Sync)
        {
            var key = $"{host}:{port}";
            RegisterIdentityLocked(identity, key);
            return _state.Pending.ContainsKey(key) || IdentityPendingLocked(identity, key);
        }
    }
    public void ThrowIfPending(string host, int port, string? identity = null)
    {
        if (IsPending(host, port, identity)) throw new CertificateTrustPendingException();
    }
    public void RegisterIdentity(string identity, string host, int port)
    {
        lock (_state.Sync) RegisterIdentityLocked(identity, $"{host}:{port}");
    }
    internal static void GuardPendingConfiguration(AppConfig candidate)
    {
        var path = Path.GetFullPath(Path.Combine(ConfigManager.AppDataPath, "trusted_certs.json"));
        if (Stores.TryGetValue(path, out var state)) GuardPendingConfiguration(candidate, state);
    }
    internal void GuardConfiguration(AppConfig candidate) => GuardPendingConfiguration(candidate, _state);
    private static void GuardPendingConfiguration(AppConfig candidate, StoreState state)
    {
        lock (state.Sync)
        {
            foreach (var binding in state.IdentityEndpoints)
            {
                if (!binding.Value.Any(k => state.Pending.ContainsKey(k) ||
                    (state.TrustedCerts.TryGetValue(k, out var pin) && (pin.RequiresLocalReview || pin.DecisionRequired)))) continue;
                if (!candidate.Servers.Any(s => binding.Key == "ftp:" + s.Id || binding.Key == "irc:" + s.Id))
                    throw new InvalidOperationException("Resolve the pending certificate decision locally before removing or replacing this server identity.");
            }
            foreach (var server in candidate.Servers)
            {
                Guard("ftp:" + server.Id, $"{server.Connection.Host}:{server.Connection.Port}", true);
                Guard("irc:" + server.Id, $"{server.Irc.Host}:{server.Irc.Port}", server.Irc.UseTls);
            }
            void Guard(string identity, string endpoint, bool tls)
            {
                if (!state.IdentityEndpoints.TryGetValue(identity, out var endpoints)) return;
                var held = endpoints.Where(k => state.Pending.ContainsKey(k) ||
                    (state.TrustedCerts.TryGetValue(k, out var pin) && (pin.RequiresLocalReview || pin.DecisionRequired))).ToArray();
                if (held.Length > 0 && (!tls || !held.Contains(endpoint, StringComparer.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("Review the pending certificate decision locally before changing this server's endpoint identity or disabling TLS.");
            }
        }
    }
    private bool IdentityPendingLocked(string? identity, string except) => identity != null &&
        _state.IdentityEndpoints.TryGetValue(identity, out var endpoints) &&
        endpoints.Any(k => !StringComparer.OrdinalIgnoreCase.Equals(k, except) && _state.Pending.ContainsKey(k));
    private List<string> IdentitiesForLocked(string key) => _state.IdentityEndpoints
        .Where(p => p.Value.Contains(key)).Select(p => p.Key).ToList();
    private void RegisterIdentityLocked(string? identity, string key)
    {
        if (string.IsNullOrEmpty(identity)) return;
        if (!_state.IdentityEndpoints.TryGetValue(identity, out var endpoints))
            _state.IdentityEndpoints[identity] = endpoints = new(StringComparer.OrdinalIgnoreCase);
        if (endpoints.Contains(key)) return;
        // Preserve the saved pin across a re-key while a decision is outstanding.
        // The alias cannot become TOFU after a restart or a local saved-pin retry.
        var held = endpoints.FirstOrDefault(k => _state.Pending.ContainsKey(k) ||
            (_state.TrustedCerts.TryGetValue(k, out var cert) && (cert.RequiresLocalReview || cert.DecisionRequired)));
        if (held != null && !_state.TrustedCerts.ContainsKey(key) && _state.TrustedCerts.TryGetValue(held, out var saved))
            _state.TrustedCerts[key] = new() { Fingerprint = saved.Fingerprint, TrustedAt = saved.TrustedAt,
                RequiresLocalReview = saved.RequiresLocalReview, DecisionRequired = saved.DecisionRequired,
                InheritedDecisionFrom = saved.InheritedDecisionFrom ?? held, Identities = [identity] };
        endpoints.Add(key);
        if (_state.TrustedCerts.TryGetValue(key, out var pin) && !pin.Identities.Contains(identity))
            pin.Identities.Add(identity);
        if (_state.TrustedCerts.ContainsKey(key)) SaveLocked();
    }
    public IReadOnlyList<PendingTrustDecision> GetPendingDecisions()
    {
        lock (_state.Sync) return _state.Pending.Values.OrderBy(d => d.DetectedAtUtc).ToArray();
    }

    // Only the local decision window calls this in production. No automation,
    // configuration, restore or hook operation exposes certificate mutation.
    internal bool ResolveFromLocalUi(string decisionId, bool accept)
    {
        lock (_state.Sync)
        {
            var decision = _state.Pending.Values.FirstOrDefault(d => d.Id == decisionId);
            if (decision == null) return false; // stale card must not accept a newer fingerprint
            if (!accept)
            {
                _state.Pending[decision.HostPort] = decision with { KeptRejected = true };
                return true;
            }
            _state.TrustedCerts[decision.HostPort] = new TrustedCert
            {
                Fingerprint = decision.NewFingerprint, TrustedAt = DateTime.UtcNow,
                Subject = decision.Subject, Issuer = decision.Issuer, NotAfter = decision.NotAfter,
                Identities = IdentitiesForLocked(decision.HostPort)
            };
            ClearInheritedHoldsLocked(decision.HostPort);
            SaveLocked();
            _state.Pending.Remove(decision.HostPort);
            Log.Information("TLS certificate change accepted by local UI");
            return true;
        }
    }
    internal void TrustCertificate(string hostPort, string fingerprint)
    {
        lock (_state.Sync)
        {
            if (MustPreservePinLocked(hostPort)) throw new InvalidOperationException("Review the pending certificate change locally.");
            if (_state.TrustedCerts.TryGetValue(hostPort, out var existing) && existing.Fingerprint != fingerprint)
                throw new InvalidOperationException("Only a local decision can replace an existing certificate pin.");
            _state.TrustedCerts[hostPort] = Describe(fingerprint, null);
            SaveLocked();
        }
    }
    internal bool RetrySavedPinFromLocalUi(string decisionId)
    {
        lock (_state.Sync)
        {
            var decision = _state.Pending.Values.FirstOrDefault(d => d.Id == decisionId);
            if (decision == null) return false;
            if (_state.TrustedCerts.TryGetValue(decision.HostPort, out var saved) && saved.RequiresLocalReview)
                return false; // an unreadable pin is not a verified saved pin
            // An operator may have restored the original certificate. This only
            // releases the parking hold; the unchanged saved pin still validates
            // the next handshake and any changed certificate parks it again.
            _state.Pending.Remove(decision.HostPort);
            if (saved != null) saved.DecisionRequired = false;
            ClearInheritedHoldsLocked(decision.HostPort);
            SaveLocked();
            Log.Information("Local UI requested TLS retry with the unchanged saved pin");
            return true;
        }
    }
    private void ClearInheritedHoldsLocked(string endpoint)
    {
        foreach (var pin in _state.TrustedCerts.Values.Where(pin =>
            StringComparer.OrdinalIgnoreCase.Equals(pin.InheritedDecisionFrom, endpoint)))
        {
            // The copied saved pin remains authoritative. A different certificate
            // at that alias still raises its own decision on the next handshake.
            pin.DecisionRequired = false;
            pin.RequiresLocalReview = false;
            pin.InheritedDecisionFrom = null;
        }
    }
    private static TrustedCert Describe(string fingerprint, X509Certificate? certificate)
    {
        using var copy = certificate == null ? null : new X509Certificate2(certificate);
        return new TrustedCert { Fingerprint = fingerprint, TrustedAt = DateTime.UtcNow,
            Subject = copy?.Subject, Issuer = copy?.Issuer, NotAfter = copy?.NotAfter };
    }
    public void ClearTrustedCertificates()
    {
        lock (_state.Sync)
        {
            foreach (var key in _state.TrustedCerts.Keys.Where(k => !MustPreservePinLocked(k)).ToArray())
                _state.TrustedCerts.Remove(key);
            SaveLocked();
        }
    }
    public void RemoveTrustedCertificate(string hostPort)
    {
        lock (_state.Sync)
        {
            if (MustPreservePinLocked(hostPort)) return; // clearing must not turn a changed pin into first use
            _state.TrustedCerts.Remove(hostPort);
            SaveLocked();
        }
    }
    public IReadOnlyDictionary<string, TrustedCert> GetTrustedCertificates()
    {
        lock (_state.Sync) return _state.TrustedCerts.ToDictionary(p => p.Key, p => new TrustedCert
        {
            Fingerprint = p.Value.Fingerprint, TrustedAt = p.Value.TrustedAt,
            Subject = p.Value.Subject, Issuer = p.Value.Issuer, NotAfter = p.Value.NotAfter,
            RequiresLocalReview = p.Value.RequiresLocalReview, DecisionRequired = p.Value.DecisionRequired,
            InheritedDecisionFrom = p.Value.InheritedDecisionFrom, Identities = [.. p.Value.Identities]
        }, StringComparer.OrdinalIgnoreCase);
    }
    private bool MustPreservePinLocked(string key) => _state.Pending.ContainsKey(key) ||
        (_state.TrustedCerts.TryGetValue(key, out var pin) && (pin.RequiresLocalReview || pin.DecisionRequired)) ||
        IdentitiesForLocked(key).Any(identity => IdentityPendingLocked(identity, key));
    private sealed record PinInventory(Dictionary<string, List<string>> Endpoints, bool UnknownPriorEndpoints,
        List<string>? PendingEndpoints = null, Dictionary<string, string>? InheritedDecisions = null);
    private void Load()
    {
        lock (_state.Sync)
        {
            if (_state.Loaded) return;
            PinInventory? inventory = null;
            try
            {
                if (File.Exists(_fingerprintFile + ".endpoints"))
                    inventory = JsonSerializer.Deserialize<PinInventory>(File.ReadAllText(_fingerprintFile + ".endpoints"), JsonOptions);
                if (inventory?.Endpoints == null || inventory.Endpoints.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Value == null || p.Value.Any(string.IsNullOrEmpty)) || inventory.PendingEndpoints?.Any(string.IsNullOrEmpty) == true ||
                    inventory.InheritedDecisions?.Any(p => string.IsNullOrWhiteSpace(p.Key) || string.IsNullOrWhiteSpace(p.Value)) == true)
                    inventory = null;
            }
            catch { /* The primary pin store remains authoritative if readable. */ }
            _state.UnknownPriorEndpoints = inventory?.UnknownPriorEndpoints == true;
            string? text = null;
            Exception? loadError = null;
            var loaded = false;
            for (var attempt = 0; attempt < 2 && !loaded; attempt++)
            {
                try
                {
                    text = File.ReadAllText(_fingerprintFile);
                    var stored = JsonSerializer.Deserialize<Dictionary<string, TrustedCert>>(text, JsonOptions)
                        ?? throw new InvalidDataException("TLS pin store is not an object.");
                    var pins = new Dictionary<string, TrustedCert>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (key, certificate) in stored)
                    {
                        if (certificate == null || certificate.Fingerprint == null || certificate.Identities == null || certificate.Identities.Any(string.IsNullOrEmpty))
                            throw new InvalidDataException("Invalid TLS pin entry.");
                        if (!pins.TryAdd(key, certificate) && pins[key].Fingerprint != certificate.Fingerprint)
                            Log.Warning("Conflicting TLS endpoint aliases found; retained the first saved pin");
                    }
                    _state.TrustedCerts = pins;
                    loaded = true;
                }
                catch (FileNotFoundException) { loaded = true; }
                catch (DirectoryNotFoundException) { loaded = true; }
                catch (Exception ex) { loadError = ex; }
            }
            if (!loaded)
            {
                // Invalid metadata (for example a date) must not hide the endpoint
                // keys. A separately persisted index covers torn/unreadable JSON.
                var recovered = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                var completeInventory = false;
                try
                {
                    using var document = JsonDocument.Parse(text!);
                    foreach (var entry in document.RootElement.EnumerateObject())
                    {
                        var identities = new List<string>();
                        if (entry.Value.ValueKind == JsonValueKind.Object && entry.Value.TryGetProperty("identities", out var ids) && ids.ValueKind == JsonValueKind.Array)
                            identities.AddRange(ids.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));
                        recovered[entry.Name] = identities;
                    }
                    completeInventory = true;
                }
                catch { }
                if (inventory != null)
                {
                    foreach (var entry in inventory.Endpoints)
                        recovered[entry.Key] = recovered.GetValueOrDefault(entry.Key, [])
                            .Concat(entry.Value).Distinct(StringComparer.Ordinal).ToList();
                    completeInventory = true;
                }
                _state.UnknownPriorEndpoints |= !completeInventory;
                foreach (var (key, identities) in recovered)
                    _state.TrustedCerts[key] = new() { RequiresLocalReview = true, Identities = identities };
                try { File.Copy(_fingerprintFile, _fingerprintFile + ".corrupt", true); }
                catch { _state.QuarantineFailed = true; }
                Log.Warning(loadError, "TLS pin store unreadable; saved endpoints require local certificate decisions. Original evidence is retained in .corrupt when available");
            }
            // An index written before a failed/crashed pin write is positive evidence
            // of prior trust. Never turn that endpoint into first use after restart.
            foreach (var entry in inventory?.Endpoints ?? [])
            {
                if (_state.TrustedCerts.TryGetValue(entry.Key, out var existing))
                    existing.Identities = existing.Identities.Concat(entry.Value).Distinct(StringComparer.Ordinal).ToList();
                else _state.TrustedCerts[entry.Key] = new() { RequiresLocalReview = true, Identities = entry.Value };
            }
            foreach (var key in inventory?.PendingEndpoints ?? [])
                if (_state.TrustedCerts.TryGetValue(key, out var pin)) pin.DecisionRequired = true;
            foreach (var entry in inventory?.InheritedDecisions ?? [])
                if (_state.TrustedCerts.TryGetValue(entry.Key, out var pin)) pin.InheritedDecisionFrom ??= entry.Value;
            foreach (var (key, pin) in _state.TrustedCerts)
                foreach (var identity in pin.Identities)
                {
                    if (!_state.IdentityEndpoints.TryGetValue(identity, out var endpoints))
                        _state.IdentityEndpoints[identity] = endpoints = new(StringComparer.OrdinalIgnoreCase);
                    endpoints.Add(key);
                }
            _state.Loaded = true;
            if (!loaded) SaveLocked();
        }
    }
    private void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_fingerprintFile)!);
            SecureFile.WriteAllTextRestricted(_fingerprintFile + ".endpoints", JsonSerializer.Serialize(
                new PinInventory(_state.TrustedCerts.ToDictionary(p => p.Key, p => p.Value.Identities), _state.UnknownPriorEndpoints,
                    _state.TrustedCerts.Where(p => p.Value.DecisionRequired).Select(p => p.Key).ToList(),
                    _state.TrustedCerts.Where(p => p.Value.InheritedDecisionFrom != null).ToDictionary(p => p.Key, p => p.Value.InheritedDecisionFrom!)), JsonOptions));
            if (_state.QuarantineFailed) return; // do not overwrite an original that could not be quarantined
            SecureFile.WriteAllTextRestricted(_fingerprintFile, JsonSerializer.Serialize(_state.TrustedCerts, JsonOptions));
        }
        catch (Exception ex)
        {
            if (!_state.WriteWarned)
            {
                _state.WriteWarned = true;
                Log.Warning(ex, "Could not save TLS pins; decisions remain in memory");
            }
        }
    }
}
public sealed record PendingTrustDecision(string Id, string HostPort, string OldFingerprint, string NewFingerprint,
    string? Subject, string? Issuer, DateTime? NotAfter, DateTime DetectedAtUtc, bool KeptRejected, string? Explanation = null)
{
    public bool CanRetrySavedPin => Explanation == null;
}
public sealed class CertificateTrustPendingException : InvalidOperationException
{
    public CertificateTrustPendingException() : base("TLS certificate change requires an explicit local decision; endpoint is parked.") { }
}
public class TrustedCert
{
    public string Fingerprint { get; set; } = "";
    public DateTime TrustedAt { get; set; }
    public string? Subject { get; set; }
    public string? Issuer { get; set; }
    public DateTime? NotAfter { get; set; }
    public bool RequiresLocalReview { get; set; }
    public bool DecisionRequired { get; set; }
    public string? InheritedDecisionFrom { get; set; }
    public List<string> Identities { get; set; } = [];
}
