using GlDrive.Config;
using GlDrive.Irc;
using Xunit;

namespace GlDrive.Tests;

public class FishCipherTests
{
    [Theory]
    [InlineData(FishMode.ECB)]
    [InlineData(FishMode.CBC)]
    public void Encrypt_then_decrypt_round_trips(FishMode mode)
    {
        const string key = "correcthorsebatterystaple";
        const string plain = "the quick brown fox jumps over 13 lazy dogs!";
        var cipher = FishCipher.Encrypt(plain, key, mode);
        Assert.True(FishCipher.IsEncrypted(cipher));
        var back = FishCipher.Decrypt(cipher, key);
        Assert.Equal(plain, back);
    }

    [Fact]
    public void Cbc_and_ecb_produce_distinct_prefixes()
    {
        const string key = "key123key123";
        var ecb = FishCipher.Encrypt("hello", key, FishMode.ECB);
        var cbc = FishCipher.Encrypt("hello", key, FishMode.CBC);
        Assert.StartsWith("+OK ", ecb);
        Assert.StartsWith("+OK *", cbc);
    }

    [Fact]
    public void Decrypt_returns_null_on_garbage()
        => Assert.Null(FishCipher.Decrypt("not encrypted text", "key123key123"));
}

public class Dh1080Tests
{
    [Fact]
    public void Both_parties_derive_the_same_key_variants()
    {
        var alice = new Dh1080();
        var bob = new Dh1080();

        var alicePub = alice.GetPublicKeyBase64();
        var bobPub = bob.GetPublicKeyBase64();

        var aliceKeys = alice.ComputeAllKeyVariants(bobPub);
        var bobKeys = bob.ComputeAllKeyVariants(alicePub);

        // The shared secret is symmetric, so every KDF variant must match.
        Assert.Equal(aliceKeys.Standard, bobKeys.Standard);
        Assert.Equal(aliceKeys.Fish, bobKeys.Fish);
        Assert.Equal(aliceKeys.FishRaw, bobKeys.FishRaw);
        Assert.False(string.IsNullOrEmpty(aliceKeys.Standard));
    }

    [Fact]
    public void Public_key_is_nonempty()
        => Assert.False(string.IsNullOrEmpty(new Dh1080().GetPublicKeyBase64()));
}
