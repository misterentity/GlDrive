using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using GlDrive.Tls;
using Xunit;

namespace GlDrive.Tests;

public sealed class CertificatePinValidationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-pins-" + Guid.NewGuid().ToString("N"));
    private readonly CertificateManager _certificates;

    public CertificatePinValidationTests() => _certificates = new CertificateManager(Path.Combine(_root, "pins.json"));

    [Fact]
    public async Task First_use_trusts_without_disposing_the_callers_certificate()
    {
        using var certificate = CreateCertificate("first");
        Assert.True(await _certificates.ValidateCertificate("fixture.test", 21, certificate));
        Assert.NotEqual(IntPtr.Zero, certificate.Handle);
        Assert.Contains("first", certificate.Subject);
        Assert.Equal(_certificates.GetFingerprint(certificate), _certificates.GetTrustedCertificates()["fixture.test:21"].Fingerprint);
        Assert.True(await _certificates.ValidateCertificate("fixture.test", 21, certificate));
    }

    [Fact]
    public async Task Changed_pin_is_rejected_and_the_existing_pin_is_preserved()
    {
        using var original = CreateCertificate("original");
        using var replacement = CreateCertificate("replacement");
        Assert.True(await _certificates.ValidateCertificate("fixture.test", 21, original));
        var before = File.ReadAllText(Path.Combine(_root, "pins.json"));
        Assert.False(await _certificates.ValidateCertificate("fixture.test", 21, replacement));
        Assert.True(_certificates.GetTrustedCertificates()["fixture.test:21"].DecisionRequired);
        Assert.Equal(_certificates.GetFingerprint(original), _certificates.GetTrustedCertificates()["fixture.test:21"].Fingerprint);
    }

    [Fact]
    public async Task Replacement_requires_an_explicit_acceptance()
    {
        using var original = CreateCertificate("original");
        using var replacement = CreateCertificate("replacement");
        Assert.True(await _certificates.ValidateCertificate("fixture.test", 21, original));
        Assert.False(await _certificates.ValidateCertificate("fixture.test", 21, replacement));
        var pending = Assert.Single(_certificates.GetPendingDecisions());
        Assert.True(_certificates.ResolveFromLocalUi(pending.Id, true));
        Assert.True(await _certificates.ValidateCertificate("fixture.test", 21, replacement));
        Assert.Equal(_certificates.GetFingerprint(replacement), _certificates.GetTrustedCertificates()["fixture.test:21"].Fingerprint);
        Assert.NotEqual(IntPtr.Zero, replacement.Handle);
    }

    [Fact]
    public async Task A_blocked_notification_does_not_hold_any_validation_gate()
    {
        using var original = CreateCertificate("original");
        using var replacement = CreateCertificate("replacement");
        await _certificates.ValidateCertificate("first.test", 21, original);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _certificates.DecisionRaised += _ => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); };
        try
        {
            Assert.False(await _certificates.ValidateCertificate("first.test", 21, replacement).WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            Assert.True(await _certificates.ValidateCertificate("other.test", 21, original).WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True(await _certificates.ValidateCertificate("other.test", 21, original).WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task Pending_is_shared_and_clear_remove_or_a_snapshot_cannot_bypass_it()
    {
        using var original = CreateCertificate("original");
        using var replacement = CreateCertificate("replacement");
        await _certificates.ValidateCertificate("fixture.test", 21, original);
        await _certificates.ValidateCertificate("fixture.test", 21, replacement);
        var other = new CertificateManager(Path.Combine(_root, "pins.json"));
        other.GetTrustedCertificates()["fixture.test:21"].Fingerprint = other.GetFingerprint(replacement);
        other.RemoveTrustedCertificate("fixture.test:21");
        other.ClearTrustedCertificates();
        Assert.Throws<CertificateTrustPendingException>(() => other.ThrowIfPending("fixture.test", 21));
        Assert.False(await other.ValidateCertificate("fixture.test", 21, replacement));
        Assert.Equal(other.GetFingerprint(original), other.GetTrustedCertificates()["fixture.test:21"].Fingerprint);
    }

    [Fact]
    public async Task Stale_card_cannot_accept_new_candidate_and_rejection_keeps_endpoint_parked()
    {
        using var original = CreateCertificate("original");
        using var replacement = CreateCertificate("replacement");
        using var newer = CreateCertificate("newer");
        await _certificates.ValidateCertificate("fixture.test", 21, original);
        await _certificates.ValidateCertificate("fixture.test", 21, replacement);
        var stale = Assert.Single(_certificates.GetPendingDecisions());
        await _certificates.ValidateCertificate("fixture.test", 21, newer);
        Assert.False(_certificates.ResolveFromLocalUi(stale.Id, true));
        var current = Assert.Single(_certificates.GetPendingDecisions());
        Assert.True(_certificates.ResolveFromLocalUi(current.Id, false));
        Assert.True(Assert.Single(_certificates.GetPendingDecisions()).KeptRejected);
        Assert.True(_certificates.IsPending("fixture.test", 21));
        Assert.False(await _certificates.ValidateCertificate("fixture.test", 21, original));
        Assert.Equal(current.Id, Assert.Single(_certificates.GetPendingDecisions()).Id);
    }

    [Fact]
    public async Task Write_failure_keeps_first_use_and_explicit_acceptance_in_memory()
    {
        Directory.CreateDirectory(_root);
        var parentFile = Path.Combine(_root, "not-a-directory");
        File.WriteAllText(parentFile, "fixture");
        var manager = new CertificateManager(Path.Combine(parentFile, "pins.json"));
        using var original = CreateCertificate("original");
        using var replacement = CreateCertificate("replacement");
        Assert.True(await manager.ValidateCertificate("fixture.test", 21, original));
        Assert.False(await manager.ValidateCertificate("fixture.test", 21, replacement));
        Assert.True(manager.ResolveFromLocalUi(Assert.Single(manager.GetPendingDecisions()).Id, true));
        Assert.True(await manager.ValidateCertificate("fixture.test", 21, replacement));
    }

    [Fact]
    public async Task Factory_and_irc_fail_before_network_and_trust_failure_is_typed()
    {
        using var original = CreateCertificate("original");
        using var replacement = CreateCertificate("replacement");
        await _certificates.ValidateCertificate("fixture.test", 21, original);
        await _certificates.ValidateCertificate("fixture.test", 21, replacement);
        var factory = new GlDrive.Ftp.FtpClientFactory(new GlDrive.Config.ServerConfig
            { Connection = new() { Host = "fixture.test", Port = 21 } }, _certificates);
        Assert.Throws<CertificateTrustPendingException>(() => factory.Create());
        await Assert.ThrowsAsync<CertificateTrustPendingException>(() => factory.CreateAndConnect());
        await Assert.ThrowsAsync<CertificateTrustPendingException>(() => factory.KillGhosts());
        using var irc = new GlDrive.Irc.IrcClient();
        await Assert.ThrowsAsync<CertificateTrustPendingException>(() => irc.ConnectAsync("fixture.test", 21, true, _certificates, default));
        Assert.Equal(GlDrive.Ftp.ConnectFailureClassifier.ConnectFailure.TrustRejected,
            GlDrive.Ftp.ConnectFailureClassifier.Classify(new Exception("wrapped", new CertificateTrustPendingException()), false, false, false));
    }

    [Fact]
    public async Task Utility_login_guard_blocks_before_any_network_and_manual_retry_cannot_override()
    {
        using var original = CreateCertificate("original");
        using var replacement = CreateCertificate("replacement");
        await _certificates.ValidateCertificate("fixture.test", 21, original);
        await _certificates.ValidateCertificate("fixture.test", 21, replacement);
        using var client = new FluentFTP.AsyncFtpClient("fixture.test", "fixture", "fixture", 21);
        GlDrive.UI.ServerEditDialog.AttachTofuValidation(client, "fixture.test", 21, _certificates);
        await Assert.ThrowsAsync<CertificateTrustPendingException>(() => GlDrive.Ftp.GuardedFtpLogin.ConnectAsync(client,
            new("fixture.test", 21, "fixture"), manualRetry: true));
    }

    [Fact]
    public async Task Trust_rejection_is_not_reported_as_remote_failure_or_authentication_failure()
    {
        var circuit = new GlDrive.Ftp.AuthenticationCircuitBreaker();
        var observed = new List<GlDrive.Ftp.FtpLoginObservation>();
        var account = new GlDrive.Ftp.FtpLoginAccount("fixture.test", 21, "fixture");
        await Assert.ThrowsAsync<CertificateTrustPendingException>(() => GlDrive.Ftp.GuardedFtpLogin.RunAsync(circuit,
            account, "fixture", _ => throw new CertificateTrustPendingException(), observe: observed.Add));
        Assert.Empty(observed);
        Assert.False(circuit.IsHeld(account));
    }

    [Fact]
    public async Task Decision_notification_does_not_inherit_automation_attribution()
    {
        using var original = CreateCertificate("original");
        using var replacement = CreateCertificate("replacement");
        await _certificates.ValidateCertificate("fixture.test", 21, original);
        var actor = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _certificates.DecisionRaised += _ => actor.TrySetResult(GlDrive.Config.ConfigWriteScope.Current.Actor);
        using (GlDrive.Config.ConfigWriteScope.Begin("automation:test"))
            Assert.False(await _certificates.ValidateCertificate("fixture.test", 21, replacement));
        Assert.Equal("application", await actor.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Explicit_retry_preserves_saved_pin_and_never_accepts_changed_certificate()
    {
        using var original = CreateCertificate("original");
        using var replacement = CreateCertificate("replacement");
        await _certificates.ValidateCertificate("fixture.test", 21, original);
        await _certificates.ValidateCertificate("fixture.test", 21, replacement);
        var decision = Assert.Single(_certificates.GetPendingDecisions());
        Assert.True(_certificates.RetrySavedPinFromLocalUi(decision.Id));
        Assert.False(_certificates.IsPending("fixture.test", 21));
        Assert.True(await _certificates.ValidateCertificate("fixture.test", 21, original));
        Assert.False(await _certificates.ValidateCertificate("fixture.test", 21, replacement));
        Assert.True(_certificates.IsPending("fixture.test", 21));
        Assert.False(_certificates.RetrySavedPinFromLocalUi(decision.Id));
        Assert.True(_certificates.IsPending("fixture.test", 21));
        Assert.Equal(_certificates.GetFingerprint(original), _certificates.GetTrustedCertificates()["fixture.test:21"].Fingerprint);
    }

    [Fact]
    public void A_stale_direct_repin_cannot_overwrite_an_existing_pin()
    {
        _certificates.TrustCertificate("fixture.test:21", new string('A', 64));
        Assert.Throws<InvalidOperationException>(() => _certificates.TrustCertificate("fixture.test:21", new string('B', 64)));
        Assert.Equal(new string('A', 64), _certificates.GetTrustedCertificates()["fixture.test:21"].Fingerprint);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_case_aliases_preserve_known_pins_and_never_become_first_use(bool conflicting)
    {
        using var original = CreateCertificate("original");
        using var alternate = CreateCertificate("alternate");
        using var replacement = CreateCertificate("replacement");
        var path = Path.Combine(_root, "legacy-pins.json");
        Directory.CreateDirectory(_root);
        var oldFingerprint = _certificates.GetFingerprint(original);
        var alternateFingerprint = _certificates.GetFingerprint(alternate);
        var stored = new Dictionary<string, TrustedCert>(StringComparer.Ordinal)
        {
            ["Fixture.test:21"] = new() { Fingerprint = oldFingerprint },
            ["fixture.test:21"] = new() { Fingerprint = conflicting ? alternateFingerprint : oldFingerprint },
            ["unrelated.test:21"] = new() { Fingerprint = alternateFingerprint }
        };
        var json = JsonSerializer.Serialize(stored, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        File.WriteAllText(path, json);

        var manager = new CertificateManager(path);
        Assert.Equal(2, manager.GetTrustedCertificates().Count);
        Assert.Equal(oldFingerprint, manager.GetTrustedCertificates()["FIXTURE.test:21"].Fingerprint);
        Assert.True(await manager.ValidateCertificate("unrelated.test", 21, alternate));
        Assert.True(await manager.ValidateCertificate("fixture.test", 21, original));
        Assert.Equal(json, File.ReadAllText(path));
        Assert.False(File.Exists(path + ".corrupt"));
        Assert.False(await manager.ValidateCertificate("FIXTURE.test", 21, conflicting ? alternate : replacement));
        Assert.Equal(oldFingerprint, manager.GetTrustedCertificates()["fixture.test:21"].Fingerprint);
        Assert.True(manager.IsPending("fixture.test", 21));
        Assert.True(await manager.ValidateCertificate("unrelated.test", 21, alternate));
        Assert.True(manager.GetTrustedCertificates()["fixture.test:21"].DecisionRequired);
    }

    private static X509Certificate2 CreateCertificate(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
