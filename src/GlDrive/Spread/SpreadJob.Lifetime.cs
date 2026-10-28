using FluentFTP;
using GlDrive.Ftp;
using Serilog;

namespace GlDrive.Spread;

public partial class SpreadJob
{
    private RacePartialState _partial = new();
    private long _lastByteProgressTicks;
    private readonly long _lifetimeStartedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
    // Guarded by _ownershipLock; created only after STOR/RETR were accepted.
    private readonly Dictionary<(string file, string destination), UploadListingProgress> _uploadListingProgress =
        new(new FileDstTupleComparer());
    private bool _cleanupOnly;
    public RaceStopReason StopReason { get; private set; }
    public int ResumeCount { get; private set; }
    public DateTime? FirstUploadAtUtc { get { lock (_ownershipLock) return _partial.FirstUploadAtUtc; } }
    internal event Action<SpreadJob>? PartialStateChanged;
    internal event Action<string>? PartialCleanupMessage;

    internal void RestorePartial(PersistedRace request)
    {
        ResumeCount = request.ResumeCount;
        _cleanupOnly = request.CleanupOnly;
        if (_cleanupOnly && Enum.TryParse<RaceStopReason>(request.StopReason, out var reason)) StopReason = reason;
        _partial = request.Partial?.Copy() ?? new();
        foreach (var directory in _partial.Directories)
        {
            _dirsCreated.Add(directory.ServerId);
            _destDelivered[directory.ServerId] = directory.DeliveredFiles.Count;
            if (directory.DeliveredFiles.Count > 0) _serversWithSuccessfulTransfer.Add(directory.ServerId);
        }
    }

    internal RacePartialState CapturePartialState()
    {
        lock (_ownershipLock)
        {
            var snapshot = _partial.Copy();
            snapshot = snapshot with { Directories = snapshot.Directories.Select(d =>
                _destStates.GetValueOrDefault(d.ServerId) == DestState.Complete
                    ? d with { CompletionConfirmed = true } : d).ToList() };
            foreach (var file in _fileInfos.Values) snapshot.ExpectedFiles[file.Name] = file.Size;
            foreach (var name in _expectedFiles) snapshot.ExpectedFiles.TryAdd(name, -1);
            return snapshot with { ExpectedCount = Math.Max(snapshot.ExpectedCount, ExpectedTotalNoLock()) };
        }
    }

    private void PartialChanged() => PartialStateChanged?.Invoke(this);

    private void RememberConfirmedCompletion()
    {
        var changed = false;
        lock (_ownershipLock)
            for (var i = 0; i < _partial.Directories.Count; i++)
            {
                var directory = _partial.Directories[i];
                if (!directory.CompletionConfirmed && _destStates.GetValueOrDefault(directory.ServerId) == DestState.Complete)
                {
                    _partial.Directories[i] = directory with { CompletionConfirmed = true };
                    changed = true;
                }
            }
        if (changed) PartialChanged();
    }

    private void RecordCreatedRoot(string serverId, string root, string createdPath, string accountIdentity)
    {
        if (!root.TrimEnd('/').Equals(createdPath.TrimEnd('/'), StringComparison.Ordinal)
            || !RaceLifetimePolicy.SafeRoot(root, ReleaseName)) return;
        lock (_ownershipLock)
        {
            if (_partial.Directories.Any(d => d.ServerId == serverId && d.RemotePath == root)) return;
            _partial.Directories.Add(new OwnedRaceDirectory
            {
                ServerId = serverId, RemotePath = root, CreatedAtUtc = DateTime.UtcNow,
                AccountIdentity = accountIdentity
            });
            _dirsCreated.Add(serverId);
        }
        // Save before starting STOR: a crash must not lose ownership provenance.
        PartialChanged();
    }

    private void RecordUploadStart(string serverId, string root, string name, long size)
    {
        lock (_ownershipLock)
        {
            _partial = _partial with { FirstUploadAtUtc = _partial.FirstUploadAtUtc ?? DateTime.UtcNow };
            var directory = _partial.Directories.FirstOrDefault(d => d.ServerId == serverId && d.RemotePath == root);
            if (directory != null && RaceLifetimePolicy.SafeRelative(name)) directory.AttemptedFiles[name] = size;
        }
        PartialChanged();
    }

    private void RecordUploadedFile(string serverId, string root, string name, long size, bool wasDupe)
    {
        lock (_ownershipLock)
        {
            if (!wasDupe && size > 0) _partial = _partial with { HasLandedPayload = true };
            var directory = _partial.Directories.FirstOrDefault(d => d.ServerId == serverId && d.RemotePath == root);
            if (directory != null)
            {
                if (!wasDupe) directory.DeliveredFiles[name] = size;
                else if (!directory.DeliveredFiles.ContainsKey(name)) directory.AttemptedFiles.Remove(name);
            }
        }
        if (!wasDupe && size > 0) Interlocked.Exchange(ref _lastByteProgressTicks, System.Diagnostics.Stopwatch.GetTimestamp());
        PartialChanged();
    }

    private void RecordByteProgress(long delta)
    {
        if (delta <= 0) return;
        Interlocked.Exchange(ref _lastByteProgressTicks, System.Diagnostics.Stopwatch.GetTimestamp());
        var firstPayload = false;
        lock (_ownershipLock)
            if (!_partial.HasLandedPayload)
            {
                _partial = _partial with { HasLandedPayload = true };
                firstPayload = true;
            }
        if (firstPayload) PartialChanged();
    }

    private async Task WatchLifetime(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                var last = Interlocked.Read(ref _lastByteProgressTicks);
                var reason = RaceLifetimePolicy.TimeoutReason(System.Diagnostics.Stopwatch.GetElapsedTime(_lifetimeStartedTicks),
                    System.Diagnostics.Stopwatch.GetElapsedTime(last == 0 ? _lifetimeStartedTicks : last), FirstUploadAtUtc,
                    DateTime.UtcNow, _spreadConfig.HardTimeoutSeconds,
                    _spreadConfig.AbsoluteRaceTimeoutMinutes, _spreadConfig.PartialRaceDeadlineHours);
                if (reason == RaceStopReason.None) continue;
                Log.Information("Spread stopping {Release}: {Reason} (resume {Resume})", ReleaseName, reason, ResumeCount);
                Stop(reason);
                return;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private void ForgetOwnedRoot(OwnedRaceDirectory directory)
    {
        lock (_ownershipLock)
        {
            _partial.Directories.RemoveAll(d => d.ServerId == directory.ServerId && d.RemotePath == directory.RemotePath);
            if (!_partial.Directories.Any(d => d.ServerId == directory.ServerId)) _dirsCreated.Remove(directory.ServerId);
        }
        PartialChanged();
    }

    /// <summary>Only called after uploads have drained, or in a cleanup-only queue turn.</summary>
    private async Task CleanupOwnedPartialAsync()
    {
        lock (_progressLock)
            if (_activeTransfers.Count > 0) return;
        var snapshot = CapturePartialState();
        foreach (var directory in snapshot.Directories)
        {
            if (directory.CompletionConfirmed)
            {
                ForgetOwnedRoot(directory);
                continue;
            }
            if (!RaceLifetimePolicy.SafeRoot(directory.RemotePath, ReleaseName))
            {
                Log.Warning("Spread cleanup refused unsafe saved root for {Release}; ownership retained", ReleaseName);
                continue;
            }
            var config = ServerConfigResolver != null ? ServerConfigResolver(directory.ServerId) : _serverConfigs.GetValueOrDefault(directory.ServerId);
            if (config == null
                || !RaceLifetimePolicy.SameAccount(directory, config.Connection))
            {
                Log.Warning("Spread cleanup refused changed account for {Release}; ownership retained for manual attention", ReleaseName);
                continue;
            }
            var pool = ResolveSpreadPool(directory.ServerId);
            if (pool == null) continue;
            PooledConnection? connection = null;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try
            {
                connection = await pool.Borrow(deadline.Token);
                // Bind to the connection actually borrowed, too: the live pool may
                // have changed between configuration resolution and acquisition.
                if (directory.AccountIdentity != RaceLifetimePolicy.AccountIdentity(connection.Client.Host,
                        connection.Client.Port, connection.Client.Credentials.UserName))
                    continue;
                var files = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                var sawComplete = false;
                var sawMissing = false;
                var unknownDirectory = false;
                var remainingEntries = 4096;

                async Task<T> Read<T>(Func<Task<T>> start)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    var read = start();
                    try { return await read.WaitAsync(deadline.Token); }
                    catch
                    {
                        var abandoned = connection;
                        abandoned.Poison("partial cleanup read failed or exceeded managed deadline");
                        if (!read.IsCompleted)
                        {
                            // LIST/DirectoryExists can issue several commands. A fixed
                            // quarantine delay cannot prove the last native recv ended.
                            // Keep the entire lease checked out until the task settles.
                            connection = null;
                            _ = ReleaseAfterRead(read, () => abandoned.DisposeAsync().AsTask());
                        }
                        throw;
                    }
                }

                async Task List(string relative, int depth)
                {
                    if (depth > 8) { unknownDirectory = true; return; }
                    var path = directory.RemotePath.TrimEnd('/') + (relative.Length == 0 ? "" : "/" + relative);
                    var listing = await Read(() => pool.UseCpsv
                        ? CpsvDataHelper.ListDirectory(connection.Client, path, pool.ControlHost, CancellationToken.None)
                        : FtpListing.GetListing(connection.Client, path, CancellationToken.None));
                    foreach (var entry in listing)
                    {
                        if (--remainingEntries < 0) { unknownDirectory = true; return; }
                        if (entry.Name is "." or "..") continue;
                        sawComplete |= RaceLifetimePolicy.IsWholeReleaseCompletionMarker(relative, entry.Name, _spreadConfig.CompletionMarkers);
                        sawMissing |= RaceLifetimePolicy.IsIncompleteReleaseMarker(entry.Name, entry.Size);
                        if (IsZipscriptArtifact(entry.Name, entry.Size)) continue;
                        var name = relative.Length == 0 ? entry.Name : relative + "/" + entry.Name;
                        if (!RaceLifetimePolicy.SafeRelative(name)) { unknownDirectory = true; continue; }
                        if (entry.Type == FtpObjectType.Directory)
                        {
                            if (!directory.AttemptedFiles.Keys.Any(f => f.StartsWith(name + "/", StringComparison.OrdinalIgnoreCase)))
                                unknownDirectory = true;
                            else await List(name, depth + 1);
                        }
                        else if (entry.Type == FtpObjectType.File) files[name] = entry.Size;
                        else unknownDirectory = true; // Never follow symlinks while deciding ownership.
                    }
                }

                // A clean negative means the old partial is already gone. Errors do
                // not establish absence and leave its durable ownership intact.
                var exists = await Read(() => connection.Client.DirectoryExists(directory.RemotePath, CancellationToken.None));
                if (!exists) { ForgetOwnedRoot(directory); continue; }
                await List("", 0);
                var verdict = RaceLifetimePolicy.CleanupVerdict(ReleaseName, directory, files,
                    sawComplete, sawMissing, snapshot, unknownDirectory);
                if (verdict is PartialCleanupVerdict.Complete or PartialCleanupVerdict.Shared or PartialCleanupVerdict.Unproven)
                {
                    ForgetOwnedRoot(directory);
                    var message = verdict switch
                    {
                        PartialCleanupVerdict.Complete => "Preserved completed release during partial cleanup",
                        PartialCleanupVerdict.Shared => "Preserved shared release during partial cleanup; other uploads are present",
                        _ => "Preserved release during partial cleanup; no SFV or missing-part evidence that it is incomplete"
                    };
                    Log.Information("Spread cleanup: {Message}: {Release} at {Path}", message, ReleaseName, directory.RemotePath);
                    PartialCleanupMessage?.Invoke(message);
                    continue;
                }
                if (verdict != PartialCleanupVerdict.Wipe) continue;
                var reply = await Read(() => connection.Client.Execute($"SITE WIPE -r {directory.RemotePath}", CancellationToken.None));
                if (!reply.Success)
                {
                    Log.Warning("Spread cleanup: WIPE rejected for {Release} ({Code}); ownership retained", ReleaseName, reply.Code);
                    continue;
                }
                ForgetOwnedRoot(directory);
                Log.Information("Spread cleanup: SITE WIPE -r {Path} for {Release} ({Reason}, resume {Resume})",
                    directory.RemotePath, ReleaseName, StopReason, ResumeCount);
                PartialCleanupMessage?.Invoke("Removed unfinished release from an owned directory");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Spread cleanup unavailable for {Release}; ownership retained for a bounded retry", ReleaseName);
            }
            finally { if (connection != null) await connection.DisposeAsync(); }
        }
    }

    internal static async Task ReleaseAfterRead(Task read, Func<Task> release)
    {
        try { await read.ConfigureAwait(false); } catch { }
        try { await release().ConfigureAwait(false); }
        catch (Exception ex) { Log.Warning(ex, "Failed to reclaim finished partial-cleanup read"); }
    }
}
