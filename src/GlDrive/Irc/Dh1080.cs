using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace GlDrive.Irc;

/// <summary>
/// DH1080 key exchange for FiSH.
/// Uses a fixed 1080-bit prime and generator 2.
/// Protocol: DH1080_INIT/DH1080_FINISH via NOTICE.
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
        var bytes = _publicKey.ToByteArray(isUnsigned: true, isBigEndian: true);
        return Convert.ToBase64String(bytes);
    }

    public string ComputeSharedSecret(string theirPubKeyBase64)
    {
        var theirBytes = Convert.FromBase64String(theirPubKeyBase64);
        var theirPubKey = new BigInteger(theirBytes, isUnsigned: true, isBigEndian: true);
        var shared = BigInteger.ModPow(theirPubKey, _privateKey, Prime);

        var sharedBytes = shared.ToByteArray(isUnsigned: true, isBigEndian: true);
        var hash = SHA256.HashData(sharedBytes);
        return Convert.ToBase64String(hash);
    }

    public static string FormatInit(string pubKeyBase64) => $"DH1080_INIT {pubKeyBase64}";
    public static string FormatFinish(string pubKeyBase64) => $"DH1080_FINISH {pubKeyBase64}";

    public static bool TryParseInit(string message, out string pubKey)
    {
        pubKey = "";
        if (!message.StartsWith("DH1080_INIT ")) return false;
        pubKey = message[12..].Trim();
        return pubKey.Length > 0;
    }

    public static bool TryParseFinish(string message, out string pubKey)
    {
        pubKey = "";
        if (!message.StartsWith("DH1080_FINISH ")) return false;
        pubKey = message[14..].Trim();
        return pubKey.Length > 0;
    }
}
