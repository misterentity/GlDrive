using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Irc;
using GlDrive.Util;
using Xunit;

namespace GlDrive.Tests;

public sealed class FishKeyStewardshipTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gldrive-fish-" + Guid.NewGuid().ToString("N"));
    private string KeyPath => Path.Combine(_root, "fish-keys-test.json");
    private static readonly byte[] Prefix = [0x70, 0x72, 0x6f, 0x74, 0x3a];
    public FishKeyStewardshipTests() => Directory.CreateDirectory(_root);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private static byte[] Protect(byte[] plain) => Prefix.Concat(plain).ToArray();
    private static byte[] Unprotect(byte[] value) => value.AsSpan().StartsWith(Prefix)
        ? value.AsSpan(Prefix.Length).ToArray() : throw new CryptographicException("Synthetic decode failure");
    private FishKeyStore Open() => new(KeyPath, Protect, Unprotect);
    private static FishKeyEntry Entry(string key = "synthetic-key") => new()
    {
        Key = key, AltKey = "synthetic-alt", DhSecretHex = "0123456789abcdef", Mode = FishMode.CBC,
        Manual = false, SetAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)
    };
    private static void AssertEntry(FishKeyEntry expected, FishKeyEntry? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Key, actual.Key); Assert.Equal(expected.AltKey, actual.AltKey);
        Assert.Equal(expected.DhSecretHex, actual.DhSecretHex); Assert.Equal(expected.Mode, actual.Mode);
        Assert.Equal(expected.Manual, actual.Manual); Assert.Equal(expected.SetAt, actual.SetAt);
    }

    [Fact]
    public void UnreadableEncryptedPrimaryIsPreservedAndPendingKeysSurviveAnotherStart()
    {
        byte[] damaged = [0x01, 0x42, 0xff, 0x00];
        File.WriteAllBytes(KeyPath, damaged);
        var store = Open();
        Assert.True(store.PersistenceHealthy);
        Assert.True(store.RecoveryRequired);
        store.SetKey("#first", "first-synthetic-key");
        Assert.Equal("first-synthetic-key", store.GetKey("#FIRST")!.Key);
        var preserved = Directory.GetFiles(_root, "*.corrupt-*");
        Assert.Equal(2, preserved.Length);
        Assert.All(preserved, file => Assert.Equal(damaged, File.ReadAllBytes(file)));
        Assert.True(File.ReadAllBytes(KeyPath).AsSpan().StartsWith(Prefix));
        var restarted = Open();
        Assert.True(restarted.PersistenceHealthy);
        Assert.True(restarted.RecoveryRequired);
        Assert.Equal("first-synthetic-key", restarted.GetKey("#first")!.Key);
        restarted.SetKey("#second", "second-synthetic-key");
        Assert.Equal(2, Open().GetAllKeys().Count);
        Assert.Single(Directory.GetFiles(_root, "fish-keys-*.json"));
    }

    [Fact]
    public void InvalidPendingFileIsPreservedBeforeFreshRuntimeKeysAreSaved()
    {
        File.WriteAllBytes(KeyPath, [1, 2]);
        File.WriteAllBytes(KeyPath + ".pending", [3, 4]);
        var store = Open();
        store.SetKey("#channel", "runtime-key");
        Assert.Equal(new byte[] { 3, 4 }, File.ReadAllBytes(Assert.Single(Directory.GetFiles(_root, "*.pending.corrupt-*"))));
        Assert.Equal("runtime-key", Open().GetKey("#channel")!.Key);
    }

    [Fact]
    public void VerifiedImportRoundTripsEveryFieldAndReturnsDetachedSnapshots()
    {
        var entry = Entry();
        var store = Open();
        Assert.True(store.TryImportVerified("#channel", entry));
        AssertEntry(entry, Open().GetKey("#CHANNEL"));
        var one = store.GetKey("#channel")!;
        one.Key = "changed"; one.Manual = true;
        var all = store.GetAllKeys();
        all["#channel"].AltKey = "changed";
        entry.DhSecretHex = "changed";
        AssertEntry(Entry(), store.GetKey("#channel"));
    }

    [Fact]
    public void ExistingSecretIsRetainedUnlessOverwriteIsChosen()
    {
        var store = Open();
        var existing = Entry("newer-local-key");
        existing.Manual = true; existing.SetAt = existing.SetAt.AddDays(1);
        Assert.True(store.TryImportVerified("#channel", existing));
        var priorBytes = File.ReadAllBytes(KeyPath);
        Assert.False(store.TryImportVerified("#channel", Entry("older-backup-key")));
        Assert.Equal(priorBytes, File.ReadAllBytes(KeyPath));
        AssertEntry(existing, store.GetKey("#channel"));
        Assert.True(store.TryImportVerified("#channel", Entry("chosen-key"), replaceExisting: true));
        AssertEntry(Entry("chosen-key"), Open().GetKey("#channel"));
        Assert.Equal(priorBytes, File.ReadAllBytes(Assert.Single(Directory.GetFiles(_root, "*.pre-import-*"))));
    }

    [Fact]
    public void SameKeyWithDifferentAgeOrManualFlagIsNotReportedAsImported()
    {
        var store = Open();
        Assert.True(store.TryImportVerified("#channel", Entry()));
        Assert.True(store.TryImportVerified("#channel", Entry()));
        var different = Entry(); different.Manual = true;
        Assert.False(store.TryImportVerified("#channel", different));
        different = Entry(); different.SetAt = different.SetAt.AddMinutes(-1);
        Assert.False(store.TryImportVerified("#channel", different));
    }

    [Fact]
    public void ImportVerificationFailureNeverReplacesWorkingPrimaryOrMemory()
    {
        Assert.True(Open().TryImportVerified("#channel", Entry("working-key")));
        var previous = File.ReadAllBytes(KeyPath);
        var fail = false;
        var store = new FishKeyStore(KeyPath, bytes => fail ? new byte[] { 0x01 } : Protect(bytes), Unprotect);
        fail = true;
        Assert.False(store.TryImportVerified("#channel", Entry("candidate-key"), replaceExisting: true));
        Assert.Equal(previous, File.ReadAllBytes(KeyPath));
        AssertEntry(Entry("working-key"), store.GetKey("#channel"));
        AssertEntry(Entry("working-key"), Open().GetKey("#channel"));
    }

    [Fact]
    public void ImportRepairsStoreAfterUnreadableSecretsAreQuarantined()
    {
        File.WriteAllBytes(KeyPath, [1, 2, 3]);
        var store = Open();
        Assert.True(store.TryImportVerified("#channel", Entry(), replaceExisting: true));
        store.SetKey("#channel", "runtime-key");
        Assert.Equal("runtime-key", store.GetKey("#channel")!.Key);
        var preserved = Directory.GetFiles(_root, "*.corrupt-*");
        Assert.Equal(2, preserved.Length);
        Assert.All(preserved, file => Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(file)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingKeysArePromotedWhenPrimaryMissingOrUnreadable(bool damagedPrimary)
    {
        var original = Open(); original.SetKey("#saved", "surviving-key");
        File.Move(KeyPath, KeyPath + ".pending");
        if (damagedPrimary) File.WriteAllBytes(KeyPath, [1, 2, 3]);
        var recovered = Open();
        Assert.True(recovered.PersistenceHealthy);
        Assert.Equal("surviving-key", recovered.GetKey("#saved")!.Key);
        Assert.False(File.Exists(KeyPath + ".pending"));
        Assert.Equal("surviving-key", Open().GetKey("#saved")!.Key);
    }

    [Fact]
    public void MalformedEntriesDoNotDiscardWorkingKeysOrRewriteTheirAge()
    {
        var json = """{"#valid":{"key":"working-key","mode":0,"manual":true,"setAt":"2026-01-02T03:04:05Z"},"":{"key":"orphan"},"#empty":{"key":""},"#null":null,"#wrong":{"key":42},"#mode":{"key":"key","mode":99},"#other":{"key":"other-key"}}""";
        File.WriteAllText(KeyPath, json);
        var store = Open();
        Assert.True(store.PersistenceHealthy);
        Assert.True(store.RecoveryRequired);
        Assert.Equal(2, store.GetAllKeys().Count);
        Assert.Equal("working-key", store.GetKey("#valid")!.Key);
        Assert.True(store.GetKey("#valid")!.Manual);
        Assert.Equal(Entry().SetAt, store.GetKey("#valid")!.SetAt);
        Assert.Equal(json, File.ReadAllText(KeyPath));
    }

    [Theory]
    [InlineData("", "value")]
    [InlineData(" ", "value")]
    [InlineData("#target", "")]
    [InlineData("#target", " ")]
    public void EmptyKeyOrTargetCannotBeStored(string target, string key)
    {
        var store = Open();
        Assert.Throws<ArgumentException>(() => store.SetKey(target, key));
        Assert.Throws<ArgumentException>(() => store.SetKeyWithAlt(target, key, "alt", FishMode.CBC));
        Assert.Throws<ArgumentException>(() => store.SetDh1080Keys(target, key, "alt", "secret", FishMode.CBC));
        Assert.Empty(store.GetAllKeys());
        Assert.False(File.Exists(KeyPath));
    }

    [Fact]
    public void MultiEntryImportAndRetryKeepOriginalSnapshotByteIdentical()
    {
        var store = Open(); store.SetKey("#a", "newer-a"); store.SetKey("#b", "newer-b");
        var original = File.ReadAllBytes(KeyPath);
        var incoming = new Dictionary<string, FishKeyEntry> { ["#a"] = Entry("older-a"), ["#b"] = Entry("older-b") };
        Assert.True(store.TryImportVerified(incoming, true));
        var safetyCopy = Assert.Single(Directory.GetFiles(_root, "*.pre-import-*"));
        Assert.Equal(original, File.ReadAllBytes(safetyCopy));
        Assert.Equal("older-a", Open().GetKey("#a")!.Key);
        Assert.Equal("older-b", Open().GetKey("#b")!.Key);
        Assert.True(store.TryImportVerified(incoming, true));
        Assert.Equal(original, File.ReadAllBytes(safetyCopy));
        Assert.Equal(2, Directory.GetFiles(_root, "*.pre-import-*").Length);
    }

    [Fact]
    public void LockedPrimaryKeepsOriginalAndPendingKeysUntilQuarantineCanFinish()
    {
        byte[] damaged = [1, 2, 3];
        File.WriteAllBytes(KeyPath, damaged);
        using (var held = new FileStream(KeyPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var store = Open();
            Assert.False(store.PersistenceHealthy);
            Assert.True(store.RecoveryRequired);
            Assert.True(FishKeyStore.HasVerifiedQuarantine(KeyPath, damaged));
            Assert.False(store.TryImportVerified("#channel", Entry(), true));
            store.SetKey("#channel", "pending-key");
            Assert.Equal(damaged, File.ReadAllBytes(KeyPath));
            Assert.True(File.Exists(KeyPath + ".pending"));
        }
        var recovered = Open();
        Assert.True(recovered.PersistenceHealthy);
        Assert.Equal("pending-key", recovered.GetKey("#channel")!.Key);
        Assert.False(File.Exists(KeyPath + ".pending"));
    }

    [Fact]
    public void OrdinaryPersistenceFailureKeepsRuntimeKeysAndLaterUpdateRetries()
    {
        var fail = true;
        var store = new FishKeyStore(KeyPath, bytes => fail ? throw new IOException() : Protect(bytes), Unprotect);
        store.SetKey("#channel", "runtime-key");
        Assert.Equal("runtime-key", store.GetKey("#channel")!.Key);
        fail = false;
        store.SetKey("#another", "second-key");
        Assert.Equal(2, Open().GetAllKeys().Count);
    }

    [Fact]
    public void ActualLegacyJsonLoadsWithoutRewritingOrRefreshingAge()
    {
        var legacy = JsonSerializer.Serialize(new Dictionary<string, FishKeyEntry> { ["#channel"] = Entry() },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        File.WriteAllText(KeyPath, legacy, new UTF8Encoding(false));
        var store = Open();
        Assert.True(store.PersistenceHealthy);
        AssertEntry(Entry(), store.GetKey("#channel"));
        Assert.Equal(legacy, File.ReadAllText(KeyPath));
    }

    [Fact]
    public void BinarySecureWriterWritesExactBytesAndLeavesNoTemporaryFile()
    {
        byte[] bytes = [0x00, 0xff, 0x41, 0x00, 0xe7];
        SecureFile.WriteAllBytesRestricted(KeyPath, bytes);
        Assert.Equal(bytes, File.ReadAllBytes(KeyPath));
        Assert.Single(Directory.GetFiles(_root));
    }
}
