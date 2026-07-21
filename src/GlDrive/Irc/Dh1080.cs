using System.Numerics;
using System.Security.Cryptography;

namespace GlDrive.Irc;

/// <summary>
/// DH1080 key exchange for FiSH.
/// Uses a fixed 1080-bit prime and generator 2.
/// Protocol: DH1080_INIT/DH1080_FINISH via NOTICE.
///
/// Wire format: base64 using the STANDARD RFC 4648 alphabet
/// ("A-Za-z0-9+/"), NOT the FiSH ECB-message alphabet ("./0-9a-zA-Z").
///
/// The canonical fish-irssi htob64/b64toh (src/base64.c) emits two
/// non-RFC-4648 quirks we must honor for interop with mIRC FiSH,
/// HexChat FiSH, KVIrc FiSH, fish-irssi, and everything derived from them:
///
/// 1) Never emits '=' padding.
/// 2) When the input bit count is a multiple of 6 (i.e. for multiples
///    of 3 bytes like the 135-byte DH1080 pubkey) the encoder's trailing
///    "flush partial sextet" loop unconditionally emits one extra 'A'
///    character after the real data. A 135-byte pubkey encodes to 181
///    chars (180 real + trailing 'A'), not 180.
///
/// The decoder (b64toh) strips trailing zero-valued chars ('A' and any
/// non-alphabet junk) before unpacking, which naturally compensates.
/// </summary>
public class Dh1080
{
    // Standard DH1080 prime (1080-bit)
    private static readonly BigInteger Prime = BigInteger.Parse(
        "0" +
        "FBE1022E23D213E8ACFA9AE8B9DFAD" +
        "A3EA6B7AC7A7B7E95AB5EB2DF85892" +
        "1FEADE95E6AC7BE7DE6ADBAB8A783E" +
        "7AF7A7FA6A2B7BEB1E72EAE2B72F9F" +
        "A2BFB2A2EFBEFAC868BADB3E828FA8" +
        "BADFADA3E4CC1BE7E8AFE85E9698A7" +
        "83EB68FA07A77AB6AD7BEB618ACF9C" +
        "A2897EB28A6189EFA07AB99A8A7FA9" +
        "AE299EFA7BA66DEAFEFBEFBF0B7D8B",
        System.Globalization.NumberStyles.HexNumber);

    private static readonly BigInteger Generator = 2;
    private const int PubKeyLengthBytes = 135; // 1080 bits

    private readonly BigInteger _privateKey;
    private readonly BigInteger _publicKey;

    public Dh1080()
    {
        // Generate random 1080-bit private key
        var privBytes = RandomNumberGenerator.GetBytes(135); // 1080 bits
        privBytes[0] &= 0x7F; // ensure positive
        _privateKey = new BigInteger(privBytes, isUnsigned: true, isBigEndian: true);
        _publicKey = BigInteger.ModPow(Generator, _privateKey, Prime);
    }

    /// <summary>
    /// Test-only constructor with a fixed private key. Used to validate our derivation
    /// byte-for-byte against reference FiSH implementations (mIRC FiSH 10 / py-fishcrypt)
    /// using deterministic known-answer vectors. NOT for production use — real exchanges
    /// must use the random-keypair constructor.
    /// </summary>
    internal Dh1080(BigInteger privateKey)
    {
        _privateKey = privateKey;
        _publicKey = BigInteger.ModPow(Generator, _privateKey, Prime);
    }

    public string GetPublicKeyBase64()
    {
        // Pad to exactly 135 bytes. BigInteger.ToByteArray(isUnsigned, isBigEndian)
        // strips leading zero bytes, so without padding a pubkey whose natural
        // representation starts with a zero byte would encode to the wrong number
        // on the peer side (decoded as value*256). Padding makes the byte stream
        // exactly 135 bytes = 1080 bits, a multiple of 6, matching fish-irssi.
        var raw = _publicKey.ToByteArray(isUnsigned: true, isBigEndian: true);
        var padded = PadLeft(raw, PubKeyLengthBytes);

        // Standard base64 of 135 bytes is 180 chars, no '=' padding (135 is a
        // multiple of 3). Append the trailing 'A' that fish-irssi's htob64 always
        // emits for byte-aligned input, producing the canonical 181-char string.
        // Peers that use the strip-trailing-zero decoder (fish-irssi, mIRC FiSH,
        // HexChat FiSH, KVIrc FiSH) will correctly discard the 'A' and decode 135
        // bytes; strict-length mIRC-style decoders expect exactly 181 chars.
        return Convert.ToBase64String(padded) + "A";
    }

    /// <summary>
    /// Returns both alphabet variants of the SHA-256-hashed derived FiSH key.
    /// </summary>
    public (string Standard, string Fish) ComputeSharedSecretBoth(string theirPubKeyBase64)
        => ComputeAllKeyVariants(theirPubKeyBase64);

    /// <summary>
    /// Returns the two Blowfish-key-string variants derived from the DH1080 shared secret.
    /// Every real FiSH client derives the key as base64(SHA256(shared)); the only ambiguity
    /// is the base64 alphabet, so we compute both and try-all-on-decrypt:
    ///   standard: base64(SHA256(shared)) using the RFC-4648 alphabet
    ///             — mIRC FiSH 10, weechat fish.py, HexChat FiSH, KVIrc, py-fishcrypt (canonical)
    ///   fish:     the same 43-char key mapped onto the FiSH ECB alphabet ("./0-9a-zA-Z")
    ///             — defensive fallback for clients that reuse the ECB alphabet for the key
    /// Both are 43 chars (32-byte SHA-256 digest → 43 base64 chars), well within Blowfish's
    /// 4..56-byte key range. Encrypt uses whichever variant produced the most recent
    /// successful decrypt (alt-promotion swap).
    ///
    /// NOTE: a former third variant (fish-base64 of the RAW ~135-byte shared secret, no
    /// SHA-256) was removed — it produced a ~180-byte string that exceeds Blowfish's 56-byte
    /// key limit, so it could never decrypt anything and instead threw ArgumentException that
    /// unwound the IRC read loop. No real client uses a raw-secret KDF.
    /// </summary>
    public (string Standard, string Fish) ComputeAllKeyVariants(string theirPubKeyBase64)
    {
        var (standard, fish, _) = ComputeAllKeyVariantsWithSecret(theirPubKeyBase64);
        return (standard, fish);
    }

    /// <summary>
    /// Same as <see cref="ComputeAllKeyVariants"/> but also returns the raw DH shared secret
    /// (hex). Storing the secret lets us derive OTHER clients' non-canonical KDF variants later
    /// (see <see cref="AlternateKdfCandidates"/>) if a peer's messages don't decrypt with the
    /// canonical key — without needing the peer to re-key.
    /// </summary>
    internal (string Standard, string Fish, string SecretHex) ComputeAllKeyVariantsWithSecret(string theirPubKeyBase64)
    {
        var sharedBytes = ComputeSharedSecretBytes(theirPubKeyBase64);
        var hash = SHA256.HashData(sharedBytes);
        var standard = Convert.ToBase64String(hash).TrimEnd('=');
        var fish = MapStandardToFishAlphabet(standard);
        return (standard, fish, Convert.ToHexString(sharedBytes));
    }

    private const int PrimeByteLength = 135; // 1080 bits

    /// <summary>
    /// Non-canonical DH1080 Blowfish-key variants some clients use, derived from the stored
    /// shared secret. Tried only when the canonical key (standard/fish) fails to decrypt a peer's
    /// messages — a peer that completes the DH handshake but derives its key differently would
    /// otherwise be permanently unreadable. All candidates are valid Blowfish key lengths
    /// (4..56 bytes). Covers the observed real-world differences:
    ///   - keeping the base64 '=' padding (44-char key) instead of trimming it (our 43-char key)
    ///   - hashing a FIXED-WIDTH (zero-padded to the prime length) shared secret instead of the
    ///     natural minimal big-endian form
    ///   - using the raw 32-byte SHA-256 digest directly as the key (no base64)
    /// </summary>
    internal static IEnumerable<string> AlternateKdfCandidates(string secretHex)
    {
        if (string.IsNullOrEmpty(secretHex)) yield break;
        byte[] natural;
        try { natural = Convert.FromHexString(secretHex); }
        catch (FormatException) { yield break; }

        var shaNatural = SHA256.HashData(natural);
        var padded = natural.Length >= PrimeByteLength ? natural : PadLeft(natural, PrimeByteLength);
        var shaPadded = SHA256.HashData(padded);

        // base64 WITH '=' padding kept (44 chars) — natural- and fixed-width shared secret
        yield return Convert.ToBase64String(shaNatural);
        yield return Convert.ToBase64String(shaPadded);
        // fixed-width shared secret, '=' trimmed (43 chars), std + fish alphabet
        var paddedStd = Convert.ToBase64String(shaPadded).TrimEnd('=');
        yield return paddedStd;
        yield return MapStandardToFishAlphabet(paddedStd);
        // raw 32-byte SHA-256 digest used directly as the Blowfish key (Latin1 round-trips bytes)
        yield return System.Text.Encoding.Latin1.GetString(shaNatural);
        yield return System.Text.Encoding.Latin1.GetString(shaPadded);
    }

    private const string StdB64Alphabet  = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    private const string FishB64Alphabet = "./0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";

    private static string MapStandardToFishAlphabet(string standardB64)
    {
        var sb = new System.Text.StringBuilder(standardB64.Length);
        foreach (var c in standardB64)
        {
            if (c == '=') continue;
            var idx = StdB64Alphabet.IndexOf(c);
            if (idx < 0) throw new CryptographicException($"Unexpected non-base64 char '{c}' in derived key");
            sb.Append(FishB64Alphabet[idx]);
        }
        return sb.ToString();
    }

    public string ComputeSharedSecret(string theirPubKeyBase64)
    {
        var sharedBytes = ComputeSharedSecretBytes(theirPubKeyBase64);
        var hash = SHA256.HashData(sharedBytes);
        // fish-irssi's htob64 on 32 bytes (256 bits, not a multiple of 6) produces
        // 43 chars with no '=' padding. Standard base64 of 32 bytes is 44 chars
        // ending in one '='. TrimEnd('=') normalizes to the fish-irssi format so
        // both sides derive the identical FiSH Blowfish key string.
        return Convert.ToBase64String(hash).TrimEnd('=');
    }

    private byte[] ComputeSharedSecretBytes(string theirPubKeyBase64)
    {
        var theirBytes = DecodeDh1080PublicKey(theirPubKeyBase64);

        var theirPubKey = new BigInteger(theirBytes, isUnsigned: true, isBigEndian: true);
        if (theirPubKey <= 1 || theirPubKey >= Prime - 1)
            throw new CryptographicException("Invalid DH1080 public key (out of safe range)");
        var shared = BigInteger.ModPow(theirPubKey, _privateKey, Prime);

        // Hash the shared secret at its NATURAL byte length (no padding) to match
        // fish-irssi: SHA256(BN_bn2bin(shared_key), len). Padding here would
        // produce different hashes than fish-irssi-compatible peers and break
        // interop for the 0.4% of exchanges whose shared secret has a leading zero byte.
        return shared.ToByteArray(isUnsigned: true, isBigEndian: true);
    }

    /// <summary>
    /// Decodes a DH1080 public key from its base64 wire form to raw big-endian bytes.
    ///
    /// This is a faithful port of the canonical fish-irssi / py-fishcrypt / mIRC FiSH 10
    /// `b64toh`/`dh1080_b64decode`: it uses the STANDARD RFC-4648 alphabet and strips the
    /// trailing flush char(s) the encoder appends for byte-aligned input, then unpacks the
    /// remaining sextets MSB-first.
    ///
    /// The previous homegrown decoder ("strip ALL trailing 'A' then pad with '='") was WRONG:
    /// when a public key's natural base64 encoding itself ended in an 'A' (≈1/64 of keys,
    /// i.e. the low 6 bits of the last byte are zero), it stripped one 'A' too many, dropped
    /// low-order bits, and derived a different shared secret than the peer. That silently
    /// broke ~1.5% of DH1080 exchanges per direction (~3% of exchanges overall) — the real
    /// "DH1080 key exchange doesn't work" symptom. This port matches the rest of the FiSH
    /// ecosystem byte-for-byte, so GlDrive fails only on the same rare inputs every FiSH
    /// client fails on (and those simply prompt a retry).
    /// </summary>
    private static byte[] DecodeDh1080PublicKey(string s)
    {
        var buf = new int[128];
        for (var idx = 0; idx < 64; idx++) buf[StdB64Alphabet[idx]] = idx;
        int Val(char c) => c < 128 ? buf[c] : 0; // non-alphabet chars decode to 0, matching the reference

        int L = s.Length;
        if (L < 2) throw new CryptographicException("Invalid DH1080 public key (too short)");

        // Strip trailing zero-value chars starting from the second-to-last position
        // (the encoder's flush char plus any genuinely-zero trailing sextets).
        for (var idx = L - 2; idx >= 0; idx--)
        {
            if (Val(s[idx]) == 0) L--;
            else break;
        }
        if (L < 2) throw new CryptographicException("Invalid DH1080 public key (too short)");

        var d = new byte[L];
        int i = 0, k = 0;
        while (true)
        {
            i++;
            if (k + 1 < L) d[i - 1] = (byte)((Val(s[k]) << 2) & 0xFF); else break;
            k++;
            if (k < L) d[i - 1] |= (byte)(Val(s[k]) >> 4); else break;
            i++;
            if (k + 1 < L) d[i - 1] = (byte)((Val(s[k]) << 4) & 0xFF); else break;
            k++;
            if (k < L) d[i - 1] |= (byte)(Val(s[k]) >> 2); else break;
            i++;
            if (k + 1 < L) d[i - 1] = (byte)((Val(s[k]) << 6) & 0xFF); else break;
            k++;
            if (k < L) d[i - 1] |= (byte)(Val(s[k]) & 0xFF); else break;
            k++;
        }
        return d[..(i - 1)];
    }

    public static string FormatInit(string pubKeyBase64) => $"DH1080_INIT {pubKeyBase64}";
    public static string FormatFinish(string pubKeyBase64) => $"DH1080_FINISH {pubKeyBase64}";

    public static bool TryParseInit(string message, out string pubKey)
    {
        pubKey = "";
        if (!message.StartsWith("DH1080_INIT ")) return false;
        pubKey = StripDhPayload(message[12..]);
        return pubKey.Length > 0;
    }

    public static bool TryParseFinish(string message, out string pubKey)
    {
        pubKey = "";
        if (!message.StartsWith("DH1080_FINISH ")) return false;
        pubKey = StripDhPayload(message[14..]);
        return pubKey.Length > 0;
    }

    /// <summary>
    /// Strips trailing mode suffix (e.g. " CBC") that some FiSH clients append to DH1080 messages.
    /// </summary>
    private static string StripDhPayload(string raw)
    {
        raw = raw.Trim();
        // Some clients send "DH1080_INIT <key> CBC" or "DH1080_FINISH <key> CBC"
        var spaceIdx = raw.IndexOf(' ');
        return spaceIdx > 0 ? raw[..spaceIdx] : raw;
    }

    /// <summary>
    /// Left-pads `source` with zeros to exactly `length` bytes. Throws if source is longer.
    /// </summary>
    private static byte[] PadLeft(byte[] source, int length)
    {
        if (source.Length == length) return source;
        if (source.Length > length)
            throw new CryptographicException(
                $"DH1080 pubkey representation ({source.Length} bytes) exceeds expected {length}");
        var padded = new byte[length];
        Buffer.BlockCopy(source, 0, padded, length - source.Length, source.Length);
        return padded;
    }
}
