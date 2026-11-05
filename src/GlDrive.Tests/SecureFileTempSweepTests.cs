using System.IO;
using GlDrive.Util;
using Xunit;

namespace GlDrive.Tests;

public sealed class SecureFileTempSweepTests : IDisposable
{
    private const string Guid32 = "0123456789abcdef0123456789abcdef";
    private static readonly DateTime Now = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gldrive-sweep-" + Guid.NewGuid().ToString("N"));

    public SecureFileTempSweepTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Make(string relative, TimeSpan age)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, Now - age);
        return path;
    }

    [Fact]
    public void Deletes_only_old_files_matching_the_staging_pattern_at_the_top_level()
    {
        var old = TimeSpan.FromHours(2);
        var orphan = Make($"downloads-bb90928a.json.{Guid32}.tmp", old);
        var orphan2 = Make($"appsettings.json.{Guid.NewGuid():N}.tmp", old);
        var keep = new[]
        {
            Make($"downloads-bb90928a.json.{Guid.NewGuid():N}.tmp", TimeSpan.FromMinutes(5)), // write in flight
            Make("pm-history-srv.json.tmp", old),                                          // fixed name, not ours
            Make("recorder-write.tmp", old),
            Make($"x.json.{Guid32[..31]}.tmp", old),                                       // 31 hex
            Make($"x.json.{Guid32.ToUpperInvariant()}.tmp", old),                          // not Guid "N" output
            Make($"x.json.{Guid32}.tmp.bak", old),
            Make($"{Guid32}.tmp", old),                                                    // no base name
            Make("downloads-bb90928a.json", old),
            Make("downloads-bb90928a.json.corrupt", old),
            Make(Path.Combine("logs", $"nested.json.{Guid32}.tmp"), old),                  // top level only
        };

        var deleted = SecureFile.SweepOrphanedTempFiles(_dir, TimeSpan.FromHours(1), nowUtc: Now);

        Assert.Equal(2, deleted);
        Assert.False(File.Exists(orphan));
        Assert.False(File.Exists(orphan2));
        Assert.All(keep, path => Assert.True(File.Exists(path), path));
    }

    [Fact]
    public void Each_sweep_is_bounded()
    {
        for (var i = 0; i < 5; i++) Make($"f{i}.json.{Guid.NewGuid():N}.tmp", TimeSpan.FromDays(1));

        Assert.Equal(3, SecureFile.SweepOrphanedTempFiles(_dir, TimeSpan.FromHours(1), maxFiles: 3, nowUtc: Now));
        Assert.Equal(2, SecureFile.SweepOrphanedTempFiles(_dir, TimeSpan.FromHours(1), maxFiles: 3, nowUtc: Now));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void The_pattern_matches_what_SecureFile_actually_stages()
    {
        // A write that dies between create and move leaves exactly this name; prove the sweep
        // recognises the real staging name rather than a hand-typed copy of it.
        var target = Path.Combine(_dir, "downloads-bb90928a.json");
        Assert.True(SecureFile.IsOrphanCandidate(Path.GetFileName(SecureFile.TempPathFor(target))));
        Assert.False(SecureFile.IsOrphanCandidate(Path.GetFileName(target)));

        SecureFile.WriteAllTextRestricted(target, "ok");
        Assert.Equal(["downloads-bb90928a.json"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void A_missing_directory_is_not_an_error()
        => Assert.Equal(0, SecureFile.SweepOrphanedTempFiles(Path.Combine(_dir, "absent"), TimeSpan.FromHours(1)));
}
