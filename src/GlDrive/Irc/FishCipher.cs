using System.Security.Cryptography;
using System.Text;
using GlDrive.Config;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace GlDrive.Irc;

/// <summary>
/// FiSH Blowfish encryption/decryption in ECB and CBC modes.
/// Message format: "+OK &lt;base64&gt;" (ECB) or "+OK *&lt;base64&gt;" (CBC).
/// </summary>
public static class FishCipher
{
    private const string EcbPrefix = "+OK ";
    private const string CbcPrefix = "+OK *";

    public static string Encrypt(string plaintext, string key, FishMode mode) =>
        mode == FishMode.CBC ? EncryptCbc(plaintext, key) : EncryptEcb(plaintext, key);

    public static string? Decrypt(string ciphertext, string key)
    {
        if (ciphertext.StartsWith(CbcPrefix))
            return DecryptCbc(ciphertext[CbcPrefix.Length..], key);
        if (ciphertext.StartsWith(EcbPrefix))
            return DecryptEcb(ciphertext[EcbPrefix.Length..], key);
        return null;
    }

    public static bool IsEncrypted(string message) =>
        message.StartsWith(EcbPrefix) || message.StartsWith(CbcPrefix);

    public static string EncryptEcb(string plaintext, string key)
    {
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var data = Encoding.UTF8.GetBytes(plaintext);

        // Pad to 8-byte boundary
        var padded = PadToBlock(data);

        var engine = new BlowfishEngine();
        engine.Init(true, new KeyParameter(keyBytes));

        var output = new byte[padded.Length];
        for (var i = 0; i < padded.Length; i += 8)
            engine.ProcessBlock(padded, i, output, i);

        return EcbPrefix + FishBase64.Encode(output);
    }

    public static string DecryptEcb(string encoded, string key)
    {
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var data = FishBase64.Decode(encoded);

        var engine = new BlowfishEngine();
        engine.Init(false, new KeyParameter(keyBytes));

        var output = new byte[data.Length];
        for (var i = 0; i < data.Length; i += 8)
            engine.ProcessBlock(data, i, output, i);

        return Encoding.UTF8.GetString(output).TrimEnd('\0');
    }

    public static string EncryptCbc(string plaintext, string key)
    {
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var data = Encoding.UTF8.GetBytes(plaintext);
        var padded = PadToBlock(data);

        // Random 8-byte IV
        var iv = RandomNumberGenerator.GetBytes(8);

        var cipher = new CbcBlockCipher(new BlowfishEngine());
        cipher.Init(true, new ParametersWithIV(new KeyParameter(keyBytes), iv));

        var output = new byte[padded.Length];
        for (var i = 0; i < padded.Length; i += 8)
            cipher.ProcessBlock(padded, i, output, i);

        // Prepend IV to ciphertext before encoding
        var withIv = new byte[8 + output.Length];
        Buffer.BlockCopy(iv, 0, withIv, 0, 8);
        Buffer.BlockCopy(output, 0, withIv, 8, output.Length);

        return CbcPrefix + FishBase64.Encode(withIv);
    }

    public static string DecryptCbc(string encoded, string key)
    {
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var data = FishBase64.Decode(encoded);

        // First 8 bytes are IV
        var iv = data[..8];
        var ciphertext = data[8..];

        var cipher = new CbcBlockCipher(new BlowfishEngine());
        cipher.Init(false, new ParametersWithIV(new KeyParameter(keyBytes), iv));

        var output = new byte[ciphertext.Length];
        for (var i = 0; i < ciphertext.Length; i += 8)
            cipher.ProcessBlock(ciphertext, i, output, i);

        return Encoding.UTF8.GetString(output).TrimEnd('\0');
    }

    private static byte[] PadToBlock(byte[] data)
    {
        var remainder = data.Length % 8;
        if (remainder == 0) return data;

        var padded = new byte[data.Length + (8 - remainder)];
        Buffer.BlockCopy(data, 0, padded, 0, data.Length);
        return padded;
    }
}
