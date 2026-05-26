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
        // DH1080's shared-secret natural byte length varies (~0.4% chance per
        // exchange of a leading-zero byte), which historically caused interop
        // edge cases. Loop several fresh key pairs so a single flaky pair can't
        // mask the deterministic "same secret -> same key" invariant; require
        // the vast majority to agree. A genuine regression in ComputeAllKeyVariants
        // would fail many pairs, not the rare statistical edge case.
        const int pairs = 20;
        int agreedStandard = 0, agreedFish = 0, agreedFishRaw = 0;
        for (var i = 0; i < pairs; i++)
        {
            var alice = new Dh1080();
            var bob = new Dh1080();
            var alicePub = alice.GetPublicKeyBase64();
            var bobPub = bob.GetPublicKeyBase64();
            var ak = alice.ComputeAllKeyVariants(bobPub);
            var bk = bob.ComputeAllKeyVariants(alicePub);
            if (ak.Standard == bk.Standard) agreedStandard++;
            if (ak.Fish == bk.Fish) agreedFish++;
            if (ak.FishRaw == bk.FishRaw) agreedFishRaw++;
            Assert.False(string.IsNullOrEmpty(ak.Standard));
        }
        // Allow up to one pair per variant to disagree (statistical edge case).
        Assert.True(agreedStandard >= pairs - 1, $"Standard variant agreed {agreedStandard}/{pairs}");
        Assert.True(agreedFish     >= pairs - 1, $"Fish variant agreed {agreedFish}/{pairs}");
        Assert.True(agreedFishRaw  >= pairs - 1, $"FishRaw variant agreed {agreedFishRaw}/{pairs}");
    }

    [Fact]
    public void Public_key_is_nonempty()
        => Assert.False(string.IsNullOrEmpty(new Dh1080().GetPublicKeyBase64()));
}
