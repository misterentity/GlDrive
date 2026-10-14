using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
        Assert.Equal(before, File.ReadAllText(Path.Combine(_root, "pins.json")));
        Assert.Equal(_certificates.GetFingerprint(original), _certificates.GetTrustedCertificates()["fixture.test:21"].Fingerprint);
    }

    [Fact]
    public async Task Replacement_requires_an_explicit_acceptance()
    {
        using var original = CreateCertificate("original");
        using var replacement = CreateCertificate("replacement");
        Assert.True(await _certificates.ValidateCertificate("fixture.test", 21, original));
        var approve = false;
        var prompts = 0;
        _certificates.CertificatePrompt += (_, _) => { prompts++; return Task.FromResult(approve); };
        Assert.False(await _certificates.ValidateCertificate("fixture.test", 21, replacement));
        approve = true;
        Assert.True(await _certificates.ValidateCertificate("fixture.test", 21, replacement));
        Assert.Equal(2, prompts);
        Assert.Equal(_certificates.GetFingerprint(replacement), _certificates.GetTrustedCertificates()["fixture.test:21"].Fingerprint);
        Assert.NotEqual(IntPtr.Zero, replacement.Handle);
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
