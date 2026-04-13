using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace GlDrive.Irc;

/// <summary>
/// DH1080 key exchange for FiSH.
/// Uses a fixed 1080-bit prime and generator 2.
/// Protocol: DH1080_INIT/DH1080_FINISH via NOTICE.
///
/// IMPORTANT: FiSH DH1080 uses a NON-STANDARD base64 alphabet
/// ("./0123456789a-zA-Z") for encoding pubkeys and the shared-secret hash.
/// This is the same alphabet as FishBase64 (for ECB messages) but with standard
/// 3-byte → 4-char grouping instead of 8-byte → 12-char. Do NOT use System.Convert
/// base64 methods here — they use the standard alphabet ("A-Za-z0-9+/=") and
/// produce output that fish-irssi / mIRC FiSH / HexChat FiSH / KVIrc FiSH will
/// reject, and fail to decode the output those clients send.
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

    // FiSH DH1080 base64 alphabet — same as FishBase64 (ECB messages), used with
    // standard 3-byte → 4-char grouping for DH1080 pubkey transport.
    private const string Dh1080Alphabet =
        "./0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";

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
        // Always pad to 135 bytes before encoding. BigInteger.ToByteArray strips
        // leading zero bytes for unsigned-mode output; without padding, a pubkey
        // with a leading zero byte encodes to a byte stream that decoders will
        // parse as `original_value * 256` — wrong value. Padding makes the
        // outgoing encoding always exactly 180 chars (45 full 4-char groups) and
        // decoders correctly reconstruct the original value.
        var raw = _publicKey.ToByteArray(isUnsigned: true, isBigEndian: true);
        var padded = PadLeft(raw, PubKeyLengthBytes);
        return Dh1080Base64Encode(padded);
    }

    public string ComputeSharedSecret(string theirPubKeyBase64)
    {
        byte[] theirBytes;
        try
        {
            theirBytes = Dh1080Base64Decode(theirPubKeyBase64);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("Invalid DH1080 public key encoding", ex);
        }

        var theirPubKey = new BigInteger(theirBytes, isUnsigned: true, isBigEndian: true);
        if (theirPubKey <= 1 || theirPubKey >= Prime - 1)
            throw new CryptographicException("Invalid DH1080 public key (out of safe range)");
        var shared = BigInteger.ModPow(theirPubKey, _privateKey, Prime);

        // Hash the shared secret at its NATURAL byte length (no padding) — this
        // matches the canonical fish-irssi implementation, which calls
        // SHA256(BN_bn2bin(shared), len). Padding here would produce different
        // hashes than fish-irssi-compatible peers.
        var sharedBytes = shared.ToByteArray(isUnsigned: true, isBigEndian: true);
        var hash = SHA256.HashData(sharedBytes);

        // Encode the 32-byte hash as FiSH DH1080 base64 (44 chars), matching what
        // fish-irssi produces. This string is stored as the FiSH key in
        // FishKeyStore and passed to FishCipher for message encryption.
        return Dh1080Base64Encode(hash);
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

    /// <summary>
    /// Encodes a byte array using the FiSH DH1080 base64 variant.
    /// 3 bytes → 4 chars, standard MSB-first grouping, but using the FiSH alphabet
    /// ("./0-9a-zA-Z") instead of the standard base64 alphabet ("A-Za-z0-9+/").
    /// Zero-pads the last group if the input length is not divisible by 3.
    /// </summary>
    private static string Dh1080Base64Encode(byte[] bytes)
    {
        var groups = (bytes.Length + 2) / 3; // round up
        var sb = new StringBuilder(groups * 4);
        for (int i = 0; i < bytes.Length; i += 3)
        {
            byte b0 = bytes[i];
            byte b1 = i + 1 < bytes.Length ? bytes[i + 1] : (byte)0;
            byte b2 = i + 2 < bytes.Length ? bytes[i + 2] : (byte)0;

            sb.Append(Dh1080Alphabet[(b0 >> 2) & 0x3F]);
            sb.Append(Dh1080Alphabet[((b0 & 0x03) << 4) | ((b1 >> 4) & 0x0F)]);
            sb.Append(Dh1080Alphabet[((b1 & 0x0F) << 2) | ((b2 >> 6) & 0x03)]);
            sb.Append(Dh1080Alphabet[b2 & 0x3F]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Decodes a FiSH DH1080 base64 string. Expects char count divisible by 4.
    /// Returns 3 bytes per 4-char group. For the canonical 180-char DH1080 pubkey
    /// this yields exactly 135 bytes.
    /// </summary>
    private static byte[] Dh1080Base64Decode(string s)
    {
        if (s.Length % 4 != 0)
            throw new FormatException($"DH1080 base64 length {s.Length} is not a multiple of 4");

        var bytes = new byte[(s.Length / 4) * 3];
        for (int i = 0, ri = 0; i < s.Length; i += 4, ri += 3)
        {
            int c0 = Dh1080Alphabet.IndexOf(s[i]);
            int c1 = Dh1080Alphabet.IndexOf(s[i + 1]);
            int c2 = Dh1080Alphabet.IndexOf(s[i + 2]);
            int c3 = Dh1080Alphabet.IndexOf(s[i + 3]);
            if (c0 < 0 || c1 < 0 || c2 < 0 || c3 < 0)
                throw new FormatException($"Invalid DH1080 base64 character at position {i}");

            bytes[ri]     = (byte)((c0 << 2) | (c1 >> 4));
            bytes[ri + 1] = (byte)(((c1 & 0x0F) << 4) | (c2 >> 2));
            bytes[ri + 2] = (byte)(((c2 & 0x03) << 6) | c3);
        }
        return bytes;
    }
}
