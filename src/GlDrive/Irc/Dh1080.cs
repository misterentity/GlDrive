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
        "BADBADA3E4CC1BE7E8AFE85E9698A7" +
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

    public string ComputeSharedSecret(string theirPubKeyBase64)
    {
        // Normalize the incoming pubkey to match fish-irssi b64toh semantics:
        // strip ALL trailing 'A' chars (B64ABC[0] = 'A', value 0), not just one.
        // fish-irssi-compatible clients (mIRC fish10, HexChat FiSH, KVIrc, weechat-fish)
        // emit a quirk trailing 'A' for byte-aligned multi-of-6-bits inputs (135-byte
        // pubkey → 181 chars). When the pubkey value's natural representation also
        // ends in zero bytes, htob64 emits MORE trailing 'A' chars from that data.
        // The canonical b64toh strips them all before bit-stream decoding, so peer
        // and we agree on a truncated bigint value. If we strip only one, we decode
        // a different bigint than peer does (for ~1/256 of exchanges), shared secret
        // diverges, FiSH key mismatches, decryption produces garbled UTF-8.
        var normalized = theirPubKeyBase64;
        while (normalized.Length > 0 && normalized[^1] == 'A')
            normalized = normalized[..^1];
        // Right-pad to multiple of 4 for Convert.FromBase64String.
        while (normalized.Length % 4 != 0)
            normalized += '=';

        byte[] theirBytes;
        try
        {
            theirBytes = Convert.FromBase64String(normalized);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("Invalid DH1080 public key encoding", ex);
        }

        var theirPubKey = new BigInteger(theirBytes, isUnsigned: true, isBigEndian: true);
        if (theirPubKey <= 1 || theirPubKey >= Prime - 1)
            throw new CryptographicException("Invalid DH1080 public key (out of safe range)");
        var shared = BigInteger.ModPow(theirPubKey, _privateKey, Prime);

        // Hash the shared secret at its NATURAL byte length (no padding) to match
        // fish-irssi: SHA256(BN_bn2bin(shared_key), len). Padding here would
        // produce different hashes than fish-irssi-compatible peers and break
        // interop for the 0.4% of exchanges whose shared secret has a leading
        // zero byte. (This is technically a fish-irssi protocol quirk we inherit.)
        var sharedBytes = shared.ToByteArray(isUnsigned: true, isBigEndian: true);
        var hash = SHA256.HashData(sharedBytes);

        // fish-irssi's htob64 on 32 bytes (256 bits, not a multiple of 6) produces
        // 43 chars with no '=' padding. Standard base64 of 32 bytes is 44 chars
        // ending in one '='. TrimEnd('=') normalizes to the fish-irssi format so
        // both sides derive the identical FiSH Blowfish key string.
        return Convert.ToBase64String(hash).TrimEnd('=');
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
