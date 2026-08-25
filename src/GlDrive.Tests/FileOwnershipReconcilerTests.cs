using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// Regression coverage for v3.10.79. Two production relay resets on 2026-08-24
/// left truncated destination files. Filename-only ownership accepted those partials,
/// removed the retry candidates, and held each race for the full 10-minute completion
/// wait despite the destination listing one fewer complete file.
/// </summary>
public sealed class FileOwnershipReconcilerTests
{
    [Fact]
    public void Smaller_destination_observation_does_not_own_the_file()
    {
        var state = new OwnershipState();

        state.Observe("source", File("part01.rar", 50_000));
        state.Observe("dest", File("part01.rar", 12_345));

        Assert.Equal(50_000, state.Infos["part01.rar"].Size);
        Assert.Contains("source", state.Ownership["part01.rar"]);
        Assert.DoesNotContain("dest", state.Ownership["part01.rar"]);
        Assert.Equal(1, state.Counts["source"]);
        Assert.False(state.Counts.ContainsKey("dest"));
    }

    [Fact]
    public void Later_full_source_revokes_a_provisionally_accepted_partial_destination()
    {
        var state = new OwnershipState();

        // Scan tasks complete in network order, so a fast destination can be processed
        // before the authoritative source on the first cycle.
        state.Observe("dest", File("part01.rar", 12_345));
        Assert.Contains("dest", state.Ownership["part01.rar"]);

        state.Observe("source", File("part01.rar", 50_000));

        Assert.Equal(50_000, state.Infos["part01.rar"].Size);
        Assert.DoesNotContain("dest", state.Ownership["part01.rar"]);
        Assert.Contains("source", state.Ownership["part01.rar"]);
        Assert.False(state.Counts.ContainsKey("dest"));
        Assert.Equal(1, state.Counts["source"]);
    }

    [Fact]
    public void In_flight_partial_is_not_indexed_as_an_owner_or_canonical_file()
    {
        var state = new OwnershipState();

        state.Observe("dest", File("part01.rar", 12_345), inFlight: true);

        Assert.Empty(state.Infos);
        Assert.Empty(state.Ownership);
        Assert.Empty(state.Counts);
        Assert.Empty(state.ObservedSizes);
    }

    [Fact]
    public void Exact_size_and_zero_byte_files_remain_valid_owners()
    {
        var state = new OwnershipState();

        state.Observe("source", File("release.nfo", 0));
        state.Observe("dest", File("release.nfo", 0));

        Assert.Equal(new[] { "dest", "source" }, state.Ownership["release.nfo"].Order());
        Assert.Equal(1, state.Counts["source"]);
        Assert.Equal(1, state.Counts["dest"]);
    }

    [Fact]
    public void Filename_matching_is_case_insensitive_across_servers()
    {
        var state = new OwnershipState();

        state.Observe("source", File("RELEASE.R00", 1_000));
        state.Observe("dest", File("release.r00", 900));

        Assert.Single(state.Infos);
        Assert.DoesNotContain("dest", state.Ownership["release.r00"]);
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

        internal void Observe(string serverId, SpreadFileInfo file, bool inFlight = false) =>
            FileOwnershipReconciler.Observe(
                serverId, file, Ownership, Infos, ObservedSizes, Counts, inFlight);
    }
}
