using System.IO;

namespace GlDrive.Downloads.Segmented;

// Shared contracts for multi-stream downloads (plexo-style). One transfer = a list of files
// pulled by several lanes at once; each lane is one connection, optionally pinned to one
// network adapter. The engine (SegmentedTransfer) is protocol-agnostic; FTP and HTTP plug in
// as ISegmentLane implementations. Downloads only — FXP racing never goes through here.
// Plan: docs/superpowers/plans/2026-10-14-multistream-downloads.md

/// <summary>Half-open byte range [Start, End).</summary>
public readonly record struct ByteRange(long Start, long End)
{
    public long Length => End - Start;
    public bool IsEmpty => End <= Start;
    public override string ToString() => $"[{Start},{End})";
}

/// <summary>
/// One file of a transfer. <paramref name="Key"/> identifies the remote object (FTP path or
/// URL). <paramref name="Validator"/> fingerprints the remote version — FTP "size:mdtm",
/// HTTP ETag or Last-Modified — and a staged partial whose validator differs is restarted
/// rather than resumed, so a file is never spliced from two versions. Null = unknown; resume
/// then trusts size alone.
/// </summary>
public sealed record TransferFile(string Key, string LocalPath, long Size, string? Validator = null);

public enum LaneState { Connecting, Active, Idle, Yielded, Failed, Done }

/// <summary>Live per-lane numbers for the downloads list ("2 streams · Ethernet ×1, race login ×1").</summary>
public sealed record LaneStats(string Label, string? NetworkId, long Bytes, double BytesPerSecond, LaneState State);

public sealed record TransferProgress(
    long DoneBytes, long TotalBytes, double BytesPerSecond, string? CurrentFile, IReadOnlyList<LaneStats> Lanes);

/// <summary>
/// One connection the engine can pull ranges through. The engine owns the lane once handed
/// over and disposes it when the transfer ends or the lane retires (for FTP that returns the
/// connection to its pool — or discards it if <see cref="Fault"/> was called).
/// </summary>
public interface ISegmentLane : IAsyncDisposable
{
    /// <summary>Human label: network name, "race login", etc.</summary>
    string Label { get; }

    /// <summary>Network this lane's bytes are charged to (pacing + allowance). Null = default route.</summary>
    string? NetworkId { get; }

    /// <summary>
    /// False when stopping a read before EOF would cost the connection (standard-PASV FTP, whose
    /// data channel is GnuTLS). The engine then only hands this lane ranges that run to EOF and
    /// never splits a range this lane holds.
    /// </summary>
    bool CanStopEarly { get; }

    /// <summary>
    /// True when the lane should leave as soon as practical (borrowed race login and the site
    /// now wants to race). The engine checks between reads, returns the unfinished remainder to
    /// the scheduler, finishes the reader early, and disposes the lane.
    /// </summary>
    bool ShouldYield { get; }

    /// <summary>
    /// Open a reader positioned at <paramref name="offset"/> of <paramref name="file"/>. The
    /// engine will read at most up to <paramref name="end"/> (which may shrink later when the
    /// range is split), then call <see cref="ISegmentReader.FinishAsync"/>.
    /// </summary>
    Task<ISegmentReader> OpenAsync(TransferFile file, long offset, long end, CancellationToken ct);

    /// <summary>
    /// The last open/read/finish on this lane failed. The lane must not reuse a connection that
    /// may be out of sync (FTP: poison). A later <see cref="OpenAsync"/> may reconnect if the
    /// lane supports it, otherwise it throws and the engine retires the lane.
    /// </summary>
    void Fault(Exception ex);
}

public interface ISegmentReader : IAsyncDisposable
{
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct);

    /// <summary>
    /// Called once the engine stops reading. <paramref name="reachedEof"/> is true when the
    /// reader was drained to end-of-file (FTP: read and validate the 226), false when the engine
    /// stopped early at a range boundary or to yield (FTP: clean abort and resync, or throw so
    /// the engine faults the lane). Must leave the lane reusable or throw.
    /// </summary>
    Task FinishAsync(bool reachedEof, CancellationToken ct);
}

/// <summary>Supplies extra lanes mid-transfer (e.g. the idle race login becoming free).</summary>
public interface ILaneSource
{
    /// <summary>Non-blocking-ish: return a lane if one is available right now, else null.</summary>
    Task<ISegmentLane?> TryAcquireAsync(CancellationToken ct);
}

/// <summary>Rate limiting across total / slow-mode / per-server / per-network buckets.</summary>
public interface ITransferPacer
{
    /// <summary>Wait until <paramref name="bytes"/> just received on <paramref name="networkId"/> fit the limits.</summary>
    ValueTask WaitAsync(string? networkId, int bytes, CancellationToken ct);
}

public sealed class SegmentedTransferOptions
{
    public int BufferSize { get; init; } = 256 * 1024;

    /// <summary>Never split an in-flight range unless both halves are at least this big.</summary>
    public long MinSplitBytes { get; init; } = 32L * 1024 * 1024;

    /// <summary>
    /// Cap on the length of a range handed out in one Take (0 = unlimited). HTTP uses a cap so
    /// requests end naturally and networks of different speeds balance; FTP leaves it unlimited
    /// so lanes take whole files and finish on a clean 226.
    /// </summary>
    public long MaxTakeBytes { get; init; }

    /// <summary>Consecutive failures before a lane is retired.</summary>
    public int MaxLaneFailures { get; init; } = 3;

    /// <summary>Base delay between a lane's retries (multiplied by the failure count).</summary>
    public TimeSpan LaneRetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How often a waiting extra-lane source is polled.</summary>
    public TimeSpan ExtraLanePollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Max lanes running at once (initial + extra).</summary>
    public int MaxLanes { get; init; } = 32;

    public ITransferPacer? Pacer { get; init; }

    /// <summary>Called for every chunk received (network id, bytes) — data-allowance accounting.</summary>
    public Action<string?, long>? OnBytes { get; init; }

    /// <summary>Called when a file is fully written and committed to its final name.</summary>
    public Action<TransferFile>? OnFileCompleted { get; init; }

    /// <summary>Minimum interval between progress reports.</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(500);
}

/// <summary>
/// Lends a site's race login (the spread pool's idle connection) to downloads while the site is
/// not racing. Implemented by SpreadManager; read-only with respect to racing state — the race
/// engine never waits on a download except for the ~1s it takes a borrowed lane to yield.
/// </summary>
public interface IRaceLoginLender
{
    /// <summary>True when <paramref name="serverId"/> has no active, starting or queued race.</summary>
    bool IsRaceIdle(string serverId);

    /// <summary>
    /// Borrow the site's idle race connection, or null when the site is racing, has no spread
    /// pool, or nothing comes free within <paramref name="wait"/>. Never creates a login beyond
    /// what the spread pool itself would.
    /// </summary>
    Task<GlDrive.Ftp.PooledConnection?> TryBorrowAsync(string serverId, TimeSpan wait, CancellationToken ct);
}

/// <summary>
/// A lane was refused by the server in a way that says "fewer connections, please" (HTTP
/// 403/429/503, no response). The engine parks the lane for <see cref="Delay"/> without
/// counting it toward the lane's failure budget — plexo's "drop to the accepted stream count,
/// regain one per minute" behavior.
/// </summary>
public sealed class LaneBackoffException(string message, TimeSpan delay, Exception? inner = null)
    : IOException(message, inner)
{
    public TimeSpan Delay { get; } = delay;
}
