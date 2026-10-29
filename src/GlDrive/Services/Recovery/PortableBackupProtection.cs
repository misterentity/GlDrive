using System.IO;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;

namespace GlDrive.Services.Recovery;

/// <summary>A versioned, bounded Argon2id/AES-256-GCM envelope. No passphrase or derived key is persisted.</summary>
internal static class PortableBackupProtection
{
    internal const int MaxPlaintextBytes = 8 * 1024 * 1024;
    internal const int HeaderBytes = 44;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("GLDRIVEBACKUP-01");

    internal static byte[] Encrypt(ReadOnlySpan<byte> plaintext, string passphrase)
    {
        ValidatePassphrase(passphrase);
        if (plaintext.Length > MaxPlaintextBytes) throw new InvalidDataException("Backup exceeds the portable backup size limit.");
        var output = new byte[HeaderBytes + 16 + plaintext.Length];
        Magic.CopyTo(output, 0);
        RandomNumberGenerator.Fill(output.AsSpan(16, 16));
        RandomNumberGenerator.Fill(output.AsSpan(32, 12));
        var key = Derive(passphrase, output.AsSpan(16, 16).ToArray());
        try
        {
            using var cipher = new AesGcm(key, 16);
            cipher.Encrypt(output.AsSpan(32, 12), plaintext, output.AsSpan(HeaderBytes + 16),
                output.AsSpan(HeaderBytes, 16), output.AsSpan(0, HeaderBytes));
            return output;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    internal static byte[] Decrypt(ReadOnlySpan<byte> envelope, string passphrase)
    {
        ValidatePassphrase(passphrase);
        if (envelope.Length < HeaderBytes + 16 || envelope.Length > MaxPlaintextBytes + HeaderBytes + 16 ||
            !envelope[..16].SequenceEqual(Magic))
            throw new InvalidDataException("Unsupported or invalid portable backup.");
        var key = Derive(passphrase, envelope.Slice(16, 16).ToArray());
        var plaintext = new byte[envelope.Length - HeaderBytes - 16];
        try
        {
            using var cipher = new AesGcm(key, 16);
            cipher.Decrypt(envelope.Slice(32, 12), envelope[(HeaderBytes + 16)..],
                envelope.Slice(HeaderBytes, 16), plaintext, envelope[..HeaderBytes]);
            return plaintext;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new InvalidDataException("The backup could not be authenticated. Check the passphrase and file.");
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static byte[] Derive(string passphrase, byte[] salt)
    {
        var password = Encoding.UTF8.GetBytes(passphrase);
        try
        {
            var generator = new Argon2BytesGenerator();
            generator.Init(new Argon2Parameters.Builder(Argon2Parameters.Argon2id)
                .WithVersion(Argon2Parameters.Version13).WithSalt(salt)
                .WithMemoryAsKB(64 * 1024).WithIterations(3).WithParallelism(1).Build());
            var key = new byte[32];
            generator.GenerateBytes(password, key);
            return key;
        }
        finally { CryptographicOperations.ZeroMemory(password); }
    }

    private static void ValidatePassphrase(string passphrase)
    {
        if (string.IsNullOrWhiteSpace(passphrase) || passphrase.Length < 12 || passphrase.Length > 4096)
            throw new ArgumentException("A passphrase of 12 to 4096 characters is required.");
    }
}
