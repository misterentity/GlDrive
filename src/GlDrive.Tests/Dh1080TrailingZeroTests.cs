using System.Numerics;
using System.Security.Cryptography;
using GlDrive.Config;
using GlDrive.Irc;
using Xunit;

namespace GlDrive.Tests;

public sealed class Dh1080TrailingZeroTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(1064)]
    [InlineData(1072)]
    public void Variable_width_peer_keys_accept_standard_and_fish_base64(int peerPrivate)
    {
        var raw = BigInteger.ModPow(2, peerPrivate, Dh1080.PrimeForTests)
            .ToByteArray(isUnsigned: true, isBigEndian: true);
        var standard = Convert.ToBase64String(raw);
        var unpadded = standard.TrimEnd('=');
        var fish = raw.Length % 3 == 0 ? unpadded + "A" : unpadded;
        var receiver = new Dh1080(new BigInteger(4001));
        var expected = Convert.ToBase64String(SHA256.HashData(
            BigInteger.ModPow(2, new BigInteger(peerPrivate) * 4001, Dh1080.PrimeForTests)
                .ToByteArray(isUnsigned: true, isBigEndian: true))).TrimEnd('=');
        foreach (var wire in new[] { standard, unpadded, fish })
            Assert.Equal(expected, receiver.ComputeSharedSecret(wire));
    }

    [Theory]
    [InlineData(FishMode.ECB)]
    [InlineData(FishMode.CBC)]
    public void Public_key_ending_in_twelve_zero_bits_preserves_the_shared_secret(FishMode mode)
    {
        // 2^6402 mod p ends in 0x1000: its wire representation ends in AAA.
        // Stripping repeated A sextets discarded an entire significant zero byte.
        var alice = new Dh1080(new BigInteger(6402));
        var bob = new Dh1080(new BigInteger(4001));
        Assert.EndsWith("AAA", alice.GetPublicKeyBase64());
        var aliceKey = alice.ComputeSharedSecret(bob.GetPublicKeyBase64());
        var bobKey = bob.ComputeSharedSecret(alice.GetPublicKeyBase64());
        Assert.Equal(aliceKey, bobKey);
        const string message = "a deterministic private message";
        Assert.Equal(message, FishCipher.Decrypt(FishCipher.Encrypt(message, aliceKey, mode), bobKey));
    }

    [Theory]
    [InlineData(8, true)]
    [InlineData(16, true)]
    [InlineData(6402, true)]
    [InlineData(8, false)]
    [InlineData(16, false)]
    [InlineData(6402, false)]
    public void Wire_decoding_preserves_zero_bytes_with_or_without_flush_marker(int peerPrivate, bool flush)
    {
        var peer = new Dh1080(new BigInteger(peerPrivate));
        var wire = peer.GetPublicKeyBase64();
        if (!flush) wire = wire[..^1];
        var receiver = new Dh1080(new BigInteger(4001));
        var expectedSecret = BigInteger.ModPow(2, new BigInteger(peerPrivate) * 4001, Dh1080.PrimeForTests);
        var expectedKey = Convert.ToBase64String(SHA256.HashData(
            expectedSecret.ToByteArray(isUnsigned: true, isBigEndian: true))).TrimEnd('=');
        Assert.Equal(expectedKey, receiver.ComputeSharedSecret(wire));
    }
}
