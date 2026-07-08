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
        try
        {
            if (ciphertext.StartsWith(CbcPrefix))
                return DecryptCbc(ciphertext[CbcPrefix.Length..], key);
            if (ciphertext.StartsWith(EcbPrefix))
                return DecryptEcb(ciphertext[EcbPrefix.Length..], key);
            return null;
        }
        catch (Exception)
        {
            // Decrypt operates on untrusted network input with a possibly-malformed key
            // (bad base64, non-block-aligned ciphertext, out-of-range Blowfish key length).
            // ANY failure just means "this key can't decrypt this message" — return null.
            // Never let a crypto exception propagate: it used to unwind the IRC read loop
            // (via DecryptWithFallback) and force a full reconnect.
            return null;
        }
    }

    public static bool IsEncrypted(string message) =>
        message.StartsWith(EcbPrefix) || message.StartsWith(CbcPrefix);

    /// <summary>
    /// Try primary key first; if its quality is below the high-confidence threshold,
    /// also try each non-empty alternate key and pick whichever beats primary by the
    /// swap margin (0.15). Returns the chosen text, the index of the winning key
    /// (0 = primary), and per-key quality scores for diagnostics.
    /// </summary>
    public static (string? Text, int WinningKeyIndex, double[] Qualities)
        DecryptWithFallback(string ciphertext, params string[] keys)
    {
        if (keys.Length == 0) return (null, -1, Array.Empty<double>());

        var qualities = new double[keys.Length];
        var decrypted = new string?[keys.Length];

        decrypted[0] = string.IsNullOrEmpty(keys[0]) ? null : Decrypt(ciphertext, keys[0]);
        qualities[0] = Quality(decrypted[0]);

        // Primary already looks like real text — don't bother with alts.
        if (qualities[0] >= 0.85)
            return (decrypted[0], 0, qualities);

        var bestAltIdx = -1;
        var bestAltQ = 0.0;
        for (var i = 1; i < keys.Length; i++)
        {
            if (string.IsNullOrEmpty(keys[i])) continue;
            decrypted[i] = Decrypt(ciphertext, keys[i]);
            qualities[i] = Quality(decrypted[i]);
            if (qualities[i] > bestAltQ)
            {
                bestAltQ = qualities[i];
                bestAltIdx = i;
            }
        }

        return bestAltQ > qualities[0] + 0.15
            ? (decrypted[bestAltIdx], bestAltIdx, qualities)
            : (decrypted[0], 0, qualities);
    }

    /// <summary>Quality threshold below which both alphabets are presumed garbage (wrong key entirely).</summary>
    public const double FailedDecryptQualityThreshold = 0.5;

    /// <summary>
    /// Fraction of chars that are printable ASCII or common IRC formatting codes.
    /// Real plaintext is typically &gt;0.85; Blowfish on wrong key produces
    /// random bytes that UTF-8-decode to a mix of valid chars and U+FFFD
    /// replacement chars. Presence of ANY U+FFFD is a hard fail signal — UTF-8
    /// decoding only emits FFFD when the byte stream is malformed, which never
    /// happens for legitimate plaintext. Returning 0 in that case ensures the
    /// caller treats the result as garbage even if the surviving chars happen
    /// to push the surface printable-ratio above the threshold.
    /// </summary>
    private static double Quality(string? s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        if (s.IndexOf('�') >= 0) return 0; // invalid UTF-8 → wrong key
        var ok = 0;
        foreach (var c in s)
        {
            // Count any NON-control character: printable ASCII AND valid non-ASCII text
            // (Cyrillic, CJK, emoji, accented Latin, …) — a correct decrypt of a non-English
            // message is real text and must score high. A wrong Blowfish key yields random
            // bytes that either fail UTF-8 (caught above) or are peppered with control chars,
            // which we do NOT count. Plus the handful of IRC formatting control codes.
            if (!char.IsControl(c)) ok++;
            else if (c is '\t' or '\n' or '\r' or '\x02' or '\x03' or '\x0F'
                          or '\x16' or '\x1D' or '\x1E' or '\x1F') ok++;
        }
        return (double)ok / s.Length;
    }

    /// <summary>
    /// Builds a Blowfish key parameter from a FiSH key string. Blowfish accepts 32..448-bit
    /// keys (4..56 bytes); FiSH clients cap the key at 56 bytes and truncate anything longer,
    /// so we do the same. This guard is load-bearing: an over-length key (e.g. a mis-derived
    /// DH1080 variant) would otherwise make BouncyCastle throw ArgumentException, which
    /// historically propagated out of the decrypt path and killed the IRC read loop.
    /// </summary>
    private static KeyParameter BlowfishKey(string key)
    {
        var keyBytes = Encoding.Latin1.GetBytes(key);
        if (keyBytes.Length > 56)
            keyBytes = keyBytes[..56];
        return new KeyParameter(keyBytes);
    }

    public static string EncryptEcb(string plaintext, string key)
    {
        var data = Encoding.UTF8.GetBytes(plaintext);

        // Pad to 8-byte boundary
        var padded = PadToBlock(data);

        var engine = new BlowfishEngine();
        engine.Init(true, BlowfishKey(key));

        var output = new byte[padded.Length];
        for (var i = 0; i < padded.Length; i += 8)
            engine.ProcessBlock(padded, i, output, i);

        return EcbPrefix + FishBase64.Encode(output);
    }

    public static string DecryptEcb(string encoded, string key)
    {
        var data = FishBase64.Decode(encoded);

        var engine = new BlowfishEngine();
        engine.Init(false, BlowfishKey(key));

        var output = new byte[data.Length];
        for (var i = 0; i < data.Length; i += 8)
            engine.ProcessBlock(data, i, output, i);

        return Encoding.UTF8.GetString(output).TrimEnd('\0');
    }

    public static string EncryptCbc(string plaintext, string key)
    {
        var data = Encoding.UTF8.GetBytes(plaintext);
        var padded = PadToBlock(data);

        // Random 8-byte IV
        var iv = RandomNumberGenerator.GetBytes(8);

        var cipher = new CbcBlockCipher(new BlowfishEngine());
        cipher.Init(true, new ParametersWithIV(BlowfishKey(key), iv));

        var output = new byte[padded.Length];
        for (var i = 0; i < padded.Length; i += 8)
            cipher.ProcessBlock(padded, i, output, i);

        // Prepend IV to ciphertext before encoding
        var withIv = new byte[8 + output.Length];
        Buffer.BlockCopy(iv, 0, withIv, 0, 8);
        Buffer.BlockCopy(output, 0, withIv, 8, output.Length);

        // FiSH CBC uses standard base64, not FiSH base64
        return CbcPrefix + Convert.ToBase64String(withIv);
    }

    public static string DecryptCbc(string encoded, string key)
    {
        // FiSH CBC uses standard base64, not FiSH base64
        var data = Convert.FromBase64String(encoded);

        // First 8 bytes are IV
        var iv = data[..8];
        var ciphertext = data[8..];

        var cipher = new CbcBlockCipher(new BlowfishEngine());
        cipher.Init(false, new ParametersWithIV(BlowfishKey(key), iv));

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
