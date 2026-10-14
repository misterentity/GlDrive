using System.Collections.Concurrent;
using System.IO;
using GlDrive.Downloads;
using GlDrive.Downloads.Segmented;
using GlDrive.UI.StreamMatrix;
using Xunit;

namespace GlDrive.Tests;

[Collection("WpfRender")]
public sealed class StreamMatrixModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gldrive-matrix-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ── Engine snapshot ──

    [Fact]
    public void Snapshot_IsConsistentCopy_WithLaneTags()
    {
        var s = new RangeScheduler([1000, 500], [new ByteRangeSet([new ByteRange(0, 100)]), new ByteRangeSet()], 0, 0);
        var a = s.Take(true, tag: new LaneTag("Ethernet", "{8A3F21C0-1111}"))!;
        var b = s.Take(true, tag: new LaneTag("race login", null))!;
        s.Reserve(a, 50);
        s.Advance(a, 50);

        var (done, inFlight) = s.Snapshot();

        Assert.Equal([new ByteRange(0, 150)], done[0]);
        Assert.Empty(done[1]);
        Assert.Equal(new SegmentSnapshot(0, 100, 150, 1000, "Ethernet", "{8A3F21C0-1111}"), inFlight[0]);
        Assert.Equal(new SegmentSnapshot(1, 0, 0, 500, "race login", null), inFlight[1]);

        // Later progress must not show through: the snapshot holds copies.
        s.Reserve(a, 200);
        s.Advance(a, 200);
        s.Release(b);
        Assert.Equal([new ByteRange(0, 150)], done[0]);
        Assert.Equal(150, inFlight[0].Position);
        Assert.Equal(2, inFlight.Count);
        Assert.Equal([new ByteRange(0, 350)], s.Snapshot().Done[0]);
    }

    [Fact]
    public async Task Transfer_ReportsMapMatchingDoneBytes()
    {
        Directory.CreateDirectory(_dir);
        var source = new Dictionary<string, byte[]>();
        TransferFile Add(string name, int size)
        {
            var data = new byte[size];
            new Random(size).NextBytes(data);
            source["/rel/" + name] = data;
            return new TransferFile("/rel/" + name, Path.Combine(_dir, name), size, $"{size}:v1");
        }
        var files = new[] { Add("a.r00", 400_000), Add("a.r01", 300_000) };
        var lanes = new[] { new SegmentedTransferTests.FakeLane(source, "eth") { ReadDelayMs = 1 }, new SegmentedTransferTests.FakeLane(source, "tether") { ReadDelayMs = 1 } };
        var reports = new ConcurrentQueue<TransferProgress>();
        var opts = new SegmentedTransferOptions
        {
            BufferSize = 8 * 1024, MinSplitBytes = 16 * 1024, MaxTakeBytes = 64 * 1024,
            ProgressInterval = TimeSpan.FromMilliseconds(5), LaneRetryDelay = TimeSpan.FromMilliseconds(1),
        };

        await new SegmentedTransfer(files, opts).RunAsync(lanes, null, new Inline(reports.Enqueue), default);

        Assert.NotEmpty(reports);
        foreach (var r in reports)
        {
            Assert.NotNull(r.Map);
            Assert.Equal(["a.r00", "a.r01"], r.Map!.Files.Select(f => f.Name));
            // The map is taken just after DoneBytes, under the scheduler lock: never behind it.
            Assert.InRange(r.Map.Files.Sum(f => f.Done.Sum(d => d.Length)), r.DoneBytes, r.TotalBytes);
            Assert.All(r.Map.InFlight, seg =>
            {
                // Same-lock snapshot: what a lane still has to fetch is never already done.
                if (seg.Position < seg.End)
                    Assert.DoesNotContain(r.Map.Files[seg.FileIndex].Done, d => d.Start < seg.End && seg.Position < d.End);
                Assert.Contains(seg.Lane, new[] { "eth", "tether" });
                Assert.Equal("net-" + seg.Lane, seg.NetworkId);
                Assert.InRange(seg.Position, seg.Start, seg.End);
            });
        }
        Assert.Contains(reports, r => r.Map!.InFlight.Count > 0);
        var last = reports.Last().Map!;
        Assert.Equal([new ByteRange(0, 400_000)], last.Files[0].Done);
    }

    private sealed class Inline(Action<TransferProgress> report) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => report(value);
    }

    // ── SegmentGridModel ──

    private static TransferMap Map(long[] sizes, ByteRange[][] done, params SegmentSnapshot[] inFlight) =>
        new(sizes.Select((s, i) => new FileMap($"f{i}", s, done[i])).ToList(), inFlight);

    [Fact]
    public void Grid_ResumedBytesAreCached_FetchedBytesBelongToTheirLane()
    {
        var m = new SegmentGridModel(10);
        // 1000 bytes, 100 per cell. [0,300) resumed; lane A wrote [300,450) and is at 450.
        m.Update(Map([1000], [[new(0, 450)]], new SegmentSnapshot(0, 300, 450, 1000, "A", null)));

        Assert.All(m.Cells.Take(3), c => Assert.Equal(CellKind.Cached, c.Kind));
        Assert.Equal(CellKind.Done, m.Cells[3].Kind);
        Assert.Equal(0, m.Cells[3].Owner);
        Assert.Equal(CellKind.Partial, m.Cells[4].Kind);
        Assert.Equal(0.5f, m.Cells[4].Fill, 3);
        Assert.Equal(CellKind.Pending, m.Cells[5].Kind);
        Assert.Equal([new HeadMarker(4, 0)], m.Heads);
    }

    [Fact]
    public void Grid_KeepsOwnersAcrossTicks_AndAttributesFinishedRangesToPreviousHolder()
    {
        var m = new SegmentGridModel(10);
        m.Update(Map([1000], [[]],
            new SegmentSnapshot(0, 0, 0, 500, "A", null),
            new SegmentSnapshot(0, 500, 500, 1000, "B", null)));
        Assert.All(m.Cells, c => Assert.Equal(CellKind.Pending, c.Kind));

        // B finished its whole range between ticks and left the map; A is halfway.
        m.Update(Map([1000], [[new(0, 250), new(500, 1000)]],
            new SegmentSnapshot(0, 0, 250, 500, "A", null)));
        Assert.Equal(0, m.Cells[0].Owner);
        Assert.Equal(0, m.Cells[1].Owner);
        Assert.All(m.Cells.Skip(5), c => { Assert.Equal(CellKind.Done, c.Kind); Assert.Equal(1, c.Owner); });

        // A finishes; owners already assigned stay put.
        m.Update(Map([1000], [[new(0, 1000)]]));
        Assert.All(m.Cells.Take(5), c => { Assert.Equal(CellKind.Done, c.Kind); Assert.Equal(0, c.Owner); });
        Assert.All(m.Cells.Skip(5), c => Assert.Equal(1, c.Owner));
        Assert.Empty(m.Heads);
    }

    [Fact]
    public void Grid_SpreadsCellsAcrossFiles_WithBoundaries()
    {
        var m = new SegmentGridModel(8);
        m.Update(Map([300, 0, 500], [[new(0, 300)], [], []], new SegmentSnapshot(2, 0, 0, 500, "A", null)));

        Assert.Equal(800, m.TotalBytes);
        Assert.Equal([3.0], m.FileBoundaries);
        Assert.All(m.Cells.Take(3), c => Assert.Equal(CellKind.Cached, c.Kind));
        Assert.Equal([new HeadMarker(3, 0)], m.Heads);
    }

    [Fact]
    public void Palette_AssignsSlotsInFirstSeenOrder()
    {
        var p = new LanePalette();
        Assert.Equal(0, p.IndexOf("Ethernet"));
        Assert.Equal(1, p.IndexOf("race login"));
        Assert.Equal(0, p.IndexOf("Ethernet"));
        for (int i = 0; i < 4; i++) p.IndexOf("n" + i);
        Assert.Equal(0, p.IndexOf("wraps"));
    }

    [Fact]
    public void History_KeepsLastSamplesAndPeak()
    {
        var h = new ThroughputHistory(3);
        h.Add(10, [5, 5]);
        h.Add(30, [10, 20]);
        h.Add(20, [20, 0]);
        h.Add(5, [5, 0]);
        Assert.Equal(3, h.Count);
        Assert.Equal([30.0, 20, 5], Enumerable.Range(0, 3).Select(h.Aggregate));
        Assert.Equal(30, h.Peak);
        Assert.True(h.HasLane(1));
        Assert.False(h.HasLane(2));
    }

    [Fact]
    public void ViewModel_BuildsHeaderLanesAndChips()
    {
        var now = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
        var vm = new StreamMatrixViewModel("Some.Release-GRP", () => now);
        var lanes = new List<LaneStats>
        {
            new("Ethernet", "{8A3F21C0-AAAA-BBBB}", 300L << 20, 40 * 1024 * 1024, LaneState.Active),
            new("race login", null, 100L << 20, 20 * 1024 * 1024, LaneState.Active),
            new("Tether", "{11112222-3333}", 1L << 20, 0, LaneState.Yielded),
        };
        long total = 1L << 30;
        var map = Map([total], [[new(0, 400L << 20)]], new SegmentSnapshot(0, 0, 400L << 20, total, "Ethernet", null));
        Assert.True(vm.Update(new DownloadProgress(400L << 20, total, 60 * 1024 * 1024, "a.r00", lanes, map), force: true));

        Assert.Equal("▚ STREAM MATRIX // 2 LINKS // AGG 60.0 MB/s // ETA 00:00:10", vm.HeaderText);
        Assert.Equal(3, vm.Lanes.Count);
        Assert.Equal("00 ETHERNET", vm.Lanes[0].Lane);
        Assert.Equal("8A3F21C0", vm.Lanes[0].Net);
        Assert.Equal("DEFAULT", vm.Lanes[1].Net);
        Assert.Equal("⇄ YIELDED", vm.Lanes[2].StateText);
        Assert.Equal(["ETHERNET", "RACE LOGIN", "TETHER"], vm.Chips.Select(c => c.Label));
        Assert.Equal("40.0 MB/s", vm.Chips[0].Detail);
        Assert.False(vm.Chips[2].IsLive);
        Assert.True(vm.HasMap);

        // Throttled: an immediate second report is dropped unless forced.
        Assert.False(vm.Update(new DownloadProgress(0, total, 0, null, lanes, map)));
        now = now.AddMilliseconds(250);
        Assert.True(vm.Update(new DownloadProgress(0, total, 0, null, lanes, map)));
    }

    [Fact]
    public void Eta_DuringAStall_ClampsInsteadOfOverflowing()
    {
        // A stall decays the smoothed rate toward zero; 1 GiB left at a few bytes/s used to
        // overflow TimeSpan and abort every matrix update (Codex UI review).
        var vm = new StreamMatrixViewModel("Some.Release-GRP");
        var ok = vm.Update(new DownloadProgress(0, 1L << 30, 1e-9), force: true);

        Assert.True(ok);
        Assert.Contains("99:59:59", vm.HeaderText);
    }
}
