using System.Security.Cryptography;
using System.Text;
using Serilog;

namespace GlDrive.Services;

/// <summary>
/// Publisher signature over checksums.sha256. Checked by <see cref="UpdateChecker"/> before
/// handing a download to the updater, and again by <see cref="UpdateInstaller"/> inside the
/// elevated process before any installed file is touched.
/// </summary>
internal static class UpdateChecksumSignature
{
    // PEM-encoded RSA public key used to verify checksums.sha256.sig signatures.
    // Private key lives in installer/keys/checksum-private.pem (gitignored).
    // Rotation: generate a new keypair, update this constant, ship the new public key in a release
    // signed by the OLD key. After widespread adoption, retire the old key.
    private const string ChecksumPublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEApLMNeSXwJZH7zeZNj/iK
        VaDDG1mn3+isBIhjnG2cKJH60WK1ImGJhTYmOGVhHKTzpKnZDJDKL6AsH3K70vG9
        GvI8MbMrX9NQpR1KCEU5NVEo3ENs27ObQ3CxFOtlJk31gcguTsUSPOQXqa0o3vec
        8yaK8w3dDHr/wJQmoupgvm+rTx94WR8ikBugK/ssB6EY6VIaBMHeAVe/EaGC0NBD
        HGuZtveNiNXfF8ezW96GE7EArrlu7LPeVDB3Jc7mFlh3RflO3kCSUF0FqwNB88hg
        MyJn6+aNupJRfki80D/tbtWLEHsDsGqIBQgywEc8Vpev6vb4XIcSgc9fNszgrFAU
        zQIDAQAB
        -----END PUBLIC KEY-----
        """;

    internal static bool Verify(string checksumText, string signatureText)
    {
        try
        {
            var sigBytes = Convert.FromBase64String(signatureText.Trim());
            using var rsa = RSA.Create();
            rsa.ImportFromPem(ChecksumPublicKeyPem);

            // BOM compatibility: PS 5.1's `Set-Content -Encoding UTF8` writes a BOM,
            // but HttpClient.GetStringAsync strips it during decode. Older releases
            // (v1.59–v1.62) signed BOM-included bytes; v1.63+ signs BOM-less bytes.
            // Try both representations so a verifier on either side of the change
            // can validate either generation of release.
            var withoutBom = Encoding.UTF8.GetBytes(checksumText);
            var withBom = new byte[withoutBom.Length + 3];
            withBom[0] = 0xEF; withBom[1] = 0xBB; withBom[2] = 0xBF;
            Buffer.BlockCopy(withoutBom, 0, withBom, 3, withoutBom.Length);

            var ok = rsa.VerifyData(withoutBom, sigBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                  || rsa.VerifyData(withBom,    sigBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            if (!ok) Log.Error("checksums.sha256 signature verification FAILED — rejecting update");
            else Log.Information("checksums.sha256 signature verified");
            return ok;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Signature verification error — rejecting update");
            return false;
        }
    }
}
