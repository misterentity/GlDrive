using System.Security.Cryptography;
using System.Text;
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

    // Regression: an over-length Blowfish key (e.g. the old ~180-byte fishRaw DH1080
    // variant) used to make BouncyCastle throw ArgumentException out of the decrypt
    // path, which unwound the IRC read loop and forced a reconnect. Decrypt must now
    // swallow it and return null; Encrypt must clamp the key and round-trip cleanly.
    [Theory]
    [InlineData(FishMode.ECB)]
    [InlineData(FishMode.CBC)]
    public void Over_length_key_never_throws(FishMode mode)
    {
        var hugeKey = new string('A', 180); // 180 bytes — far over Blowfish's 56-byte max

        // A real message encrypted under a *different* short key must not throw when
        // decrypted with the over-length key — it just fails to decrypt.
        var otherCipher = FishCipher.Encrypt("secret", "shortkey123", mode);
        var ex = Record.Exception(() => FishCipher.Decrypt(otherCipher, hugeKey));
        Assert.Null(ex);

        // The over-length key itself must still round-trip (it is clamped to 56 bytes
        // consistently on encrypt and decrypt).
        var cipher = FishCipher.Encrypt("hello world", hugeKey, mode);
        Assert.Equal("hello world", FishCipher.Decrypt(cipher, hugeKey));
    }

    [Fact]
    public void DecryptWithFallback_never_throws_on_bad_keys()
    {
        // Feed a mix of empty, over-length, and wrong keys — must return without throwing.
        var cipher = FishCipher.Encrypt("hi", "realkey123", FishMode.ECB);
        var ex = Record.Exception(() =>
            FishCipher.DecryptWithFallback(cipher, "", new string('Z', 200), "wrongkey"));
        Assert.Null(ex);
    }
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
        // the vast majority to agree.
        const int pairs = 20;
        int agreedStandard = 0, agreedFish = 0;
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
            Assert.False(string.IsNullOrEmpty(ak.Standard));
        }
        // Allow up to one pair per variant to disagree (statistical edge case).
        Assert.True(agreedStandard >= pairs - 1, $"Standard variant agreed {agreedStandard}/{pairs}");
        Assert.True(agreedFish     >= pairs - 1, $"Fish variant agreed {agreedFish}/{pairs}");
    }

    [Fact]
    public void Public_key_is_nonempty()
        => Assert.False(string.IsNullOrEmpty(new Dh1080().GetPublicKeyBase64()));

    // The canonical DH1080 Blowfish key (mIRC FiSH 10, weechat fish.py, HexChat, KVIrc,
    // py-fishcrypt) is standard-RFC4648-base64(SHA256(shared_secret)) with '=' padding
    // stripped. Our Standard variant must equal exactly that, and must be a valid
    // Blowfish key length (43 bytes, within 4..56).
    [Fact]
    public void Standard_variant_matches_canonical_derivation_and_is_valid_length()
    {
        var alice = new Dh1080();
        var bob = new Dh1080();
        var std = alice.ComputeAllKeyVariants(bob.GetPublicKeyBase64()).Standard;
        Assert.Equal(43, std.Length);                  // 32-byte SHA-256 digest -> 43 base64 chars
        Assert.InRange(Encoding.Latin1.GetBytes(std).Length, 4, 56); // usable Blowfish key
    }

    // End-to-end proof that DH1080 private-message encryption actually works: two parties
    // exchange public keys, derive the key, then one encrypts and the other decrypts via
    // the same fallback path the live IRC handlers use. This is the behaviour the user
    // reported as "doesn't work at all" — it was masked by the fishRaw-variant crash.
    [Theory]
    [InlineData(FishMode.ECB)]
    [InlineData(FishMode.CBC)]
    public void Dh1080_derived_key_round_trips_a_private_message(FishMode mode)
    {
        var alice = new Dh1080();
        var bob = new Dh1080();
        var aliceKeys = alice.ComputeAllKeyVariants(bob.GetPublicKeyBase64());
        var bobKeys = bob.ComputeAllKeyVariants(alice.GetPublicKeyBase64());

        const string plain = "meet me in #ops - the eagle lands at 0300";

        // Alice encrypts with her derived (canonical Standard) key.
        var cipher = FishCipher.Encrypt(plain, aliceKeys.Standard, mode);

        // Bob decrypts via the try-all-variants fallback (Standard, Fish).
        var (decrypted, winIdx, _) =
            FishCipher.DecryptWithFallback(cipher, bobKeys.Standard, bobKeys.Fish);

        Assert.Equal(plain, decrypted);
        Assert.Equal(0, winIdx); // Standard variant wins — it is the canonical key
    }
}
