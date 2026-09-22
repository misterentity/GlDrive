using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Regression coverage for v3.10.107. On 2026-09-07 a source briefly listed a file
/// that belonged to a different release (an E45 volume dropped into the E44 dir and
/// deleted by the site's zipscript seconds later). The race's file set was add-only,
/// so the phantom stayed "missing" through 56 RETR 550s, five completion sweeps that
/// reinitialised every pool, five ghost-kills that severed live sessions, and finally
/// a SITE WIPE of the 27 good files the destination already held. A file that a site
/// previously listed and no longer lists must lose that site as an owner, and a file
/// nobody lists any more must leave the race set.
/// </summary>
public sealed class SourceListingPruneTests
{
    [Fact]
    public void File_absent_from_a_fresh_listing_loses_that_site_as_owner()
    {
        var state = new OwnershipState();
        state.Observe("source", File("a.rar", 100), File("b.rar", 100));

        var dropped = state.Prune("source", File("a.rar", 100));

        Assert.Equal(new[] { "b.rar" }, dropped);
        Assert.False(state.Ownership.ContainsKey("b.rar"));
        Assert.False(state.Infos.ContainsKey("b.rar"));
        Assert.False(state.ObservedSizes.ContainsKey(("b.rar", "source")));
        Assert.Equal(1, state.Counts["source"]);
        Assert.Single(state.Infos);
    }

    [Fact]
    public void File_still_listed_by_another_site_stays_in_the_race_set()
    {
        var state = new OwnershipState();
        state.Observe("source", File("a.rar", 100), File("b.rar", 100));
        state.Observe("dest", File("b.rar", 100));

        var dropped = state.Prune("source", File("a.rar", 100));

        Assert.Empty(dropped);
        Assert.True(state.Infos.ContainsKey("b.rar"));
        Assert.Equal(new[] { "dest" }, state.Ownership["b.rar"]);
        Assert.Equal(1, state.Counts["source"]);
        Assert.Equal(1, state.Counts["dest"]);
    }

    [Fact]
    public void Empty_listing_is_a_moved_directory_not_evidence_about_files()
    {
        // The E28 race on 2026-09-07 saw "returned 0 files" when glftpd moved the
        // release /incoming -> /recent; the relocation follower handles that. Pruning
        // on an empty listing would empty the race set and declare a false complete.
        var state = new OwnershipState();
        state.Observe("source", File("a.rar", 100), File("b.rar", 100));

        var dropped = state.Prune("source");

        Assert.Empty(dropped);
        Assert.Equal(2, state.Infos.Count);
        Assert.Equal(2, state.Counts["source"]);
    }

    [Fact]
    public void Site_that_never_owned_the_file_cannot_revoke_it()
    {
        var state = new OwnershipState();
        state.Observe("source", File("a.rar", 100), File("b.rar", 100));

        var dropped = state.Prune("dest", File("a.rar", 100));

        Assert.Empty(dropped);
        Assert.Equal(2, state.Infos.Count);
        Assert.Equal(2, state.Counts["source"]);
    }

    [Fact]
    public void Phantom_volume_leaves_the_set_and_the_destination_reads_complete()
    {
        // 2026-09-07 04:20:45 — source lists 28 (27 real + 1 phantom); dest already
        // holds 27; 04:21:08 — source lists 27 again.
        var state = new OwnershipState();
        var real = Enumerable.Range(0, 27).Select(i => File($"v.r{i:00}", 1_000)).ToArray();
        state.Observe("source", real.Append(File("phantom.r01", 1_000)).ToArray());
        state.Observe("dest", real);
        Assert.Equal(28, state.Infos.Count);

        var dropped = state.Prune("source", real);

        Assert.Equal(new[] { "phantom.r01" }, dropped);
        Assert.Equal(27, state.Infos.Count);
        Assert.True(state.Counts["dest"] >= state.Infos.Count);
    }

    [Fact]
    public void Matching_is_case_insensitive_like_ownership()
    {
        var state = new OwnershipState();
        state.Observe("source", File("A.RAR", 100));

        var dropped = state.Prune("source", File("a.rar", 100));

        Assert.Empty(dropped);
        Assert.Single(state.Infos);
    }

    [Fact]
    public void Listing_taken_before_a_transfer_completed_cannot_revoke_it()
    {
        // v3.10.127 — 2026-09-21 14:07: zephyr's LIST returned at :06.338 without r65,
        // r65's FXP completed at :09.772 (ownership recorded), and the listing was
        // APPLIED at :11.288. Prune read the pre-completion snapshot as "deleted",
        // the file was re-selected and re-sent into a dupe-skip — 55 times that day.
        var state = new OwnershipState();
        state.Observe("source", File("a.rar", 100), File("b.rar", 100));
        state.Observe("dest", File("a.rar", 100));
        state.RecordTransfer("dest", "b.rar"); // completion after the LIST snapshot

        var dropped = state.Prune("dest", confirmedAfterListing: n => n == "b.rar", File("a.rar", 100));

        Assert.Empty(dropped);
        Assert.Contains("dest", state.Ownership["b.rar"]);
        Assert.Equal(2, state.Counts["dest"]);
    }

    [Fact]
    public void Protection_covers_only_the_names_confirmed_after_the_listing()
    {
        var state = new OwnershipState();
        state.Observe("source", File("a.rar", 100), File("b.rar", 100), File("c.rar", 100));
        state.Observe("dest", File("a.rar", 100), File("c.rar", 100));
        state.RecordTransfer("dest", "b.rar");

        // c.rar was genuinely deleted from dest; b.rar just landed after the snapshot.
        state.Prune("dest", confirmedAfterListing: n => n == "b.rar", File("a.rar", 100));

        Assert.Contains("dest", state.Ownership["b.rar"]);
        Assert.DoesNotContain("dest", state.Ownership["c.rar"]);
        Assert.Equal(2, state.Counts["dest"]);
    }

    private static SpreadFileInfo File(string name, long size) => new()
    {
        Name = name,
        FullPath = "/release/" + name,
        Size = size
    };

    private sealed class OwnershipState
    {
        internal Dictionary<string, HashSet<string>> Ownership { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<string, SpreadFileInfo> Infos { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<(string fileName, string serverId), long> ObservedSizes { get; } =
            new(new FileDstTupleComparer());
        internal Dictionary<string, int> Counts { get; } = new(StringComparer.Ordinal);

        internal void Observe(string serverId, params SpreadFileInfo[] files)
        {
            foreach (var f in files)
                FileOwnershipReconciler.Observe(serverId, f, Ownership, Infos, ObservedSizes, Counts, inFlight: false);
        }

        internal List<string> Prune(string serverId, params SpreadFileInfo[] listing) =>
            FileOwnershipReconciler.Prune(serverId, listing, Ownership, Infos, ObservedSizes, Counts);

        internal List<string> Prune(string serverId, Func<string, bool> confirmedAfterListing,
            params SpreadFileInfo[] listing) =>
            FileOwnershipReconciler.Prune(serverId, listing, Ownership, Infos, ObservedSizes, Counts,
                confirmedAfterListing);

        // Mirrors SpreadJob's transfer-completion bookkeeping.
        internal void RecordTransfer(string serverId, string name)
        {
            ObservedSizes[(name, serverId)] = Infos[name].Size;
            if (Ownership[name].Add(serverId))
                Counts[serverId] = Counts.GetValueOrDefault(serverId) + 1;
        }
    }
}
