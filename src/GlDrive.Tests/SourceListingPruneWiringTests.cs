using System.IO;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Pins the production wiring of <c>FileOwnershipReconciler.Prune</c> into
/// <c>SpreadJob.ProcessFiles</c>. The prune itself is unit-tested; these checks
/// ensure every processed listing actually reaches it, that a nuke-aborted (partial)
/// walk never does, and that the dropped names also leave the skiplist-action map.
/// </summary>
public sealed class SourceListingPruneWiringTests
{
    private const string Call =
        "FileOwnershipReconciler.Prune(serverId, files, _fileOwnership, _fileInfos, _observedFileSizes, _serverFileCount)";

    private static readonly string Source = ReadSpreadJob();

    [Fact]
    public void ProcessFiles_prunes_against_the_listing_it_just_applied()
    {
        var first = Source.IndexOf(Call, StringComparison.Ordinal);
        Assert.True(first >= 0, "Prune is not wired into SpreadJob.");
        Assert.Equal(-1, Source.IndexOf(Call, first + 1, StringComparison.Ordinal));
    }

    [Fact]
    public void Nuke_aborted_partial_walk_never_prunes()
    {
        var prune = Source.IndexOf(Call, StringComparison.Ordinal);
        Assert.True(prune > 0, "Prune is not wired.");
        var guard = Source.LastIndexOf("if (!_isNuked)", prune, StringComparison.Ordinal);
        Assert.True(guard > 0 && prune - guard < 200,
            "Prune must sit directly under an `if (!_isNuked)` guard: a nuke marker aborts the walk mid-directory, leaving a partial listing.");
    }

    [Fact]
    public void Dropped_names_leave_the_skiplist_action_map()
    {
        var prune = Source.IndexOf(Call, StringComparison.Ordinal);
        var remove = Source.IndexOf("_fileActions.Remove(", prune, StringComparison.Ordinal);
        Assert.True(remove > prune && remove - prune < 600, "Dropped names must also be removed from _fileActions.");
    }

    private static string ReadSpreadJob()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (; dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "src", "GlDrive", "Spread", "SpreadJob.cs");
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException("SpreadJob.cs not found from " + Directory.GetCurrentDirectory());
    }
}
