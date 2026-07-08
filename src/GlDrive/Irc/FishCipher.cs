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

    public static string Encrypt(string plaintext, string key, FishMode mode, Encoding? charset = null) =>
        mode == FishMode.CBC ? EncryptCbc(plaintext, key, charset) : EncryptEcb(plaintext, key, charset);

    public static string? Decrypt(string ciphertext, string key, Encoding? fallbackCharset = null)
    {
        try
        {
            if (ciphertext.StartsWith(CbcPrefix))
                return DecryptCbc(ciphertext[CbcPrefix.Length..], key, fallbackCharset);
            if (ciphertext.StartsWith(EcbPrefix))
                return DecryptEcb(ciphertext[EcbPrefix.Length..], key, fallbackCharset);
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
        => DecryptWithFallback(ciphertext, null, keys);

    /// <summary>
    /// As above, with an optional legacy fallback charset (e.g. windows-1251) applied when a
    /// decrypt is not valid UTF-8 but looks like text — for peers whose client doesn't use UTF-8.
    /// </summary>
    public static (string? Text, int WinningKeyIndex, double[] Qualities)
        DecryptWithFallback(string ciphertext, Encoding? fallbackCharset, params string[] keys)
    {
        if (keys.Length == 0) return (null, -1, Array.Empty<double>());

        var qualities = new double[keys.Length];
        var decrypted = new string?[keys.Length];

        decrypted[0] = string.IsNullOrEmpty(keys[0]) ? null : Decrypt(ciphertext, keys[0], fallbackCharset);
        qualities[0] = Quality(decrypted[0]);

        // Primary already looks like real text — don't bother with alts.
        if (qualities[0] >= 0.85)
            return (decrypted[0], 0, qualities);

        var bestAltIdx = -1;
        var bestAltQ = 0.0;
        for (var i = 1; i < keys.Length; i++)
        {
            if (string.IsNullOrEmpty(keys[i])) continue;
            decrypted[i] = Decrypt(ciphertext, keys[i], fallbackCharset);
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

    public static string EncryptEcb(string plaintext, string key, Encoding? charset = null)
    {
        var data = (charset ?? Encoding.UTF8).GetBytes(plaintext);

        // Pad to 8-byte boundary
        var padded = PadToBlock(data);

        var engine = new BlowfishEngine();
        engine.Init(true, BlowfishKey(key));

        var output = new byte[padded.Length];
        for (var i = 0; i < padded.Length; i += 8)
            engine.ProcessBlock(padded, i, output, i);

        return EcbPrefix + FishBase64.Encode(output);
    }

    public static string DecryptEcb(string encoded, string key, Encoding? fallbackCharset = null)
    {
        var data = FishBase64.Decode(encoded);

        var engine = new BlowfishEngine();
        engine.Init(false, BlowfishKey(key));

        var output = new byte[data.Length];
        for (var i = 0; i < data.Length; i += 8)
            engine.ProcessBlock(data, i, output, i);

        return BytesToText(output, fallbackCharset);
    }

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);

    /// <summary>
    /// Turns decrypted plaintext bytes into a string. Prefers UTF-8 (modern clients, and a strong
    /// correct-key signal: random Blowfish output rarely forms valid UTF-8). If the bytes are NOT
    /// valid UTF-8 and a legacy <paramref name="fallbackCharset"/> is configured AND they look like
    /// text (not random garbage), decode with that charset — this is how a peer whose client uses a
    /// legacy 8-bit charset (e.g. windows-1251 Cyrillic) becomes readable. Otherwise return the
    /// lossy UTF-8 (with U+FFFD) so the quality check rejects it as a wrong key.
    /// </summary>
    private static string BytesToText(byte[] bytes, Encoding? fallbackCharset)
    {
        // Strip FiSH's trailing NUL block padding.
        var len = bytes.Length;
        while (len > 0 && bytes[len - 1] == 0) len--;
        var span = bytes.AsSpan(0, len);

        try { return StrictUtf8.GetString(span); }
        catch (DecoderFallbackException) { /* not valid UTF-8 — fall through */ }

        // Legacy 8-bit charset fallback. Decode with the charset, then check the resulting CHARS
        // (charset-aware) so real punctuation that lives at 0x80-0x9F in windows-125x — apostrophe,
        // em/en dash, curly quotes, ellipsis, euro — is accepted, while random wrong-key bytes
        // (which decode to control chars / U+FFFD) are rejected. Caller uses this for DISPLAY only
        // and never lets it drive key decisions, since an 8-bit charset can't reliably prove a key.
        if (fallbackCharset != null && len >= MinLegacyTextBytes)
        {
            var legacy = fallbackCharset.GetString(span);
            if (LooksLikeText(legacy)) return legacy;
        }

        return Encoding.UTF8.GetString(span); // lossy (U+FFFD) → Quality() scores 0
    }

    /// <summary>
    /// Minimum decrypted length (bytes) before the legacy-charset fallback is even considered.
    /// Below this a random wrong-key block is too easily mistaken for short real text.
    /// </summary>
    private const int MinLegacyTextBytes = 16;

    /// <summary>
    /// True if the decoded string looks like real text: no U+FFFD (invalid in the charset) and no
    /// control characters beyond common whitespace + IRC formatting. Charset-aware — evaluates the
    /// decoded CHARS, so windows-125x typographic glyphs at 0x80-0x9F (which decode to real Unicode
    /// punctuation, not controls) pass, while random bytes (which decode to control chars) fail.
    /// </summary>
    private static bool LooksLikeText(string s)
    {
        foreach (var c in s)
        {
            if (c == '�') return false;
            if (char.IsControl(c) && c is not ('\t' or '\n' or '\r' or '\x02' or '\x03' or '\x0F'
                                               or '\x16' or '\x1D' or '\x1E' or '\x1F'))
                return false;
        }
        return true;
    }

    public static string EncryptCbc(string plaintext, string key, Encoding? charset = null)
    {
        var data = (charset ?? Encoding.UTF8).GetBytes(plaintext);
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

    public static string DecryptCbc(string encoded, string key, Encoding? fallbackCharset = null)
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

        return BytesToText(output, fallbackCharset);
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
