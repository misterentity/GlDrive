using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Serilog;

namespace GlDrive.Services;

internal sealed record UpdateProbationStatus(bool RollbackAvailable, string? PreviousVersion,
    DateTime? RetainUntilUtc, string Message, string? Generation);

internal sealed record UpdateRollbackLaunch(bool Started, string Message);

/// <summary>Local installed-file recovery. It never decides whether a server may connect.</summary>
internal static class UpdateProbation
{
    internal const string MetadataDirectory = ".update-recovery";
    internal const string MutationLockFile = ".update-recovery.lock";
    internal static readonly TimeSpan Retention = TimeSpan.FromHours(24);
    private static int _warned;

    internal static UpdateProbationStore InstalledStore() => ForInstalledRoot(AppContext.BaseDirectory);

    // ApplyUpdate has already validated this root, including its constrained legacy handoff.
    // Rollback always uses InstalledStore and accepts no root from the command line.
    internal static UpdateProbationStore ForInstalledRoot(string root) => new(root,
        UpdateChecker.CurrentVersion, UpdateInstallationTrust.IsProtected,
        path => AssemblyName.GetAssemblyName(path).Version, WriteTrustedReceipt, CreateTrustedDirectory);

    internal static UpdateProbationStatus GetStatus() => InstalledStore().GetStatus(DateTime.UtcNow);

    internal static void WarnOnce(Exception error)
    {
        if (Interlocked.Exchange(ref _warned, 1) == 0)
            Log.Warning(error, "Update recovery bookkeeping is unavailable; keeping previous files. Normal operation and update checks continue");
    }

    internal static void LogRollbackFailure(string message)
    {
        // The elevated rollback helper never appends to user-writable Temp/ProgramData paths.
        // Atomic replacement beneath the protected root also avoids following file hardlinks.
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, MetadataDirectory, "rollback.log");
            WriteTrustedReceipt(path, DateTime.UtcNow.ToString("O") + " " + message + Environment.NewLine);
        }
        catch { /* The local error dialog remains available even if diagnostics cannot be saved. */ }
    }

    internal static bool IsMetadataPath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative.Equals(MutationLockFile, StringComparison.OrdinalIgnoreCase) ||
            relative.Equals(MetadataDirectory, StringComparison.OrdinalIgnoreCase) ||
            relative.StartsWith(MetadataDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void CreateTrustedDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            if (!UpdateInstallationTrust.IsProtected(path)) throw new IOException("Recovery directory is not protected");
            return;
        }
        var parent = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(parent)) CreateTrustedDirectory(parent);
        if (!UpdateInstallationTrust.IsProtected(parent)) throw new IOException("Recovery parent is not protected");
        Directory.CreateDirectory(path);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    private static void WriteTrustedReceipt(string path, string content)
    {
        CreateTrustedDirectory(Path.GetDirectoryName(path)!);
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.Read, AccessControlType.Allow));
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = FileSystemAclExtensions.Create(new FileInfo(temporary), FileMode.CreateNew,
                       FileSystemRights.WriteData | FileSystemRights.ReadData, FileShare.None, 4096, FileOptions.WriteThrough, security))
            {
                stream.Write(System.Text.Encoding.UTF8.GetBytes(content));
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

/// <summary>
/// Receipt and payload paths are always relative to the fixed installed root. Production callers
/// supply the Windows trust check; tests supply a temp-root predicate and synthetic version reader.
/// </summary>
internal sealed class UpdateProbationStore
{
    internal sealed record Payload(string Path, long Length, string Sha256);
    internal sealed record Receipt(int Format, string Generation, string PreviousVersion, string CurrentVersion,
        DateTime CreatedUtc, bool Consumed, Payload[] PreviousFiles, Payload[] InstalledFiles);

    private const int MaximumFiles = 8192;
    private const int MaximumReceipts = 128;
    private const int MaximumReceiptBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string _root;
    private readonly Version _current;
    private readonly Func<string, bool> _trusted;
    private readonly Func<string, Version?> _version;
    private readonly Action<string, string> _write;
    private readonly Action<string> _mkdir;
    private string Metadata => Path.Combine(_root, UpdateProbation.MetadataDirectory);

    internal UpdateProbationStore(string root, Version current, Func<string, bool> trusted,
        Func<string, Version?> version, Action<string, string>? write = null, Action<string>? mkdir = null)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _current = UpdateChecker.Normalize(current);
        _trusted = trusted;
        _version = version;
        _write = write ?? ((path, text) => File.WriteAllText(path, text));
        _mkdir = mkdir ?? (path => Directory.CreateDirectory(path));
    }

    internal UpdateProbationStatus GetStatus(DateTime now)
    {
        try
        {
            RequireTrusted(_root);
            var receipts = ReadReceipts();
            var matching = receipts.Where(r => IsCurrent(r)).OrderByDescending(r => r.CreatedUtc).ToArray();
            if (matching.Length > 0)
            {
                var receipt = matching[0];
                if (receipt.Consumed) return Status(receipt, false, "This rollback was already used. It cannot run again.");
                if (!IsWithinProbation(receipt, now)) return Status(receipt, false, "The 24-hour rollback period has ended or its timestamp is invalid.");
                VerifyPayloads(receipt);
                return Status(receipt, true, "The previous installed build is retained for 24 hours. Rollback requires local confirmation and administrator approval.");
            }
            var legacy = DiscoverLegacy(now);
            if (legacy != null)
                return Status(legacy, true, "Protected files from the previous updater were found. Rollback will validate and seal them after administrator approval.");
            return new(false, null, null, "No complete protected previous build is available for rollback.", null);
        }
        catch (Exception error)
        {
            UpdateProbation.WarnOnce(error);
            return new(false, null, null, "Previous files are retained, but recovery evidence or installation permissions could not be verified. Normal operation remains available.", null);
        }
    }

    // Positive, persisted evidence only. Read failures must not indefinitely pin a release.
    internal DateTime? HoldUntil(DateTime now)
    {
        try
        {
            RequireTrusted(_root);
            return ReadReceipts().Where(r => IsCurrent(r) && !r.Consumed && IsWithinProbation(r, now))
                .Select(r => (DateTime?)r.CreatedUtc.Add(UpdateProbation.Retention)).Max();
        }
        catch (Exception error) { UpdateProbation.WarnOnce(error); return null; }
    }

    internal bool HasPreviousFiles()
    {
        try
        {
            RequireTrusted(_root);
            return EnumeratePayloadFiles(skipUntrusted: true).Any(path => path.EndsWith(".old", StringComparison.OrdinalIgnoreCase)) ||
                Directory.Exists(Metadata) && Directory.EnumerateDirectories(Metadata, "rollback-*", SearchOption.TopDirectoryOnly).Any();
        }
        catch (Exception error) { UpdateProbation.WarnOnce(error); return false; }
    }

    internal bool RecordInstallation(IEnumerable<(string Original, string Backup)> backups, DateTime now, bool mutationLockHeld = false)
    {
        try
        {
            RequireTrusted(_root);
            using var operationLock = mutationLockHeld ? null : AcquireMutationLock();
            var captured = backups.ToArray();
            if (captured.Length == 0) return false;
            var generations = captured.Select(b => ParseBackup(Path.GetRelativePath(_root, b.Backup))?.Generation)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (generations.Length != 1 || generations[0] == null) throw new InvalidDataException("Mixed update generations");
            var receipt = Capture(generations[0]!, captured.Select(b => Path.GetRelativePath(_root, b.Original)), now);
            SaveVerified(receipt);
            return true;
        }
        catch (Exception error) { UpdateProbation.WarnOnce(error); return false; }
    }

    /// <summary>Adopts a legacy generation once; missing or malformed evidence only preserves files.</summary>
    internal void ObserveLegacy(DateTime now)
    {
        try
        {
            RequireTrusted(_root);
            using var operationLock = AcquireMutationLock();
            ObserveLegacyUnderLock(now);
        }
        catch (Exception error) { UpdateProbation.WarnOnce(error); }
    }

    private void ObserveLegacyUnderLock(DateTime now)
    {
        if (ReadReceipts().Any(IsCurrent)) return;
        var receipt = DiscoverLegacy(now);
        if (receipt != null) SaveVerified(receipt);
    }

    internal (int Deleted, int Retained) Cleanup(DateTime now)
    {
        var pass = CleanupPass(now);
        return (pass.Deleted, pass.Retained);
    }

    internal (int Deleted, int Retained) CleanupWithRetry(TimeSpan initialDelay, TimeSpan retryWindow,
        TimeSpan retryDelay, Func<DateTime>? clock = null, Action<TimeSpan>? sleep = null)
    {
        clock ??= () => DateTime.UtcNow;
        sleep ??= Thread.Sleep;
        if (initialDelay > TimeSpan.Zero) sleep(initialDelay);
        var timer = Stopwatch.StartNew();
        var deleted = 0;
        while (true)
        {
            var pass = CleanupPass(clock());
            deleted += pass.Deleted;
            // Expected 24-hour retention, unknown evidence, changed hashes and permanent
            // permission failures are not retryable locks. Only positive sharing violations retry.
            if (pass.Retryable == 0 || timer.Elapsed >= retryWindow) return (deleted, pass.Retained);
            var remaining = retryWindow - timer.Elapsed;
            if (remaining <= TimeSpan.Zero) return (deleted, pass.Retained);
            sleep(retryDelay < remaining ? retryDelay : remaining);
        }
    }

    private (int Deleted, int Retained, int Retryable) CleanupPass(DateTime now)
    {
        var deleted = 0;
        var retryable = 0;
        try
        {
            RequireTrusted(_root);
            using var operationLock = AcquireMutationLock();
            try { ObserveLegacyUnderLock(now); }
            catch (Exception error) { UpdateProbation.WarnOnce(error); }
            List<Receipt> receipts;
            try { receipts = ReadReceipts(); }
            catch (Exception error) { UpdateProbation.WarnOnce(error); receipts = []; }
            var referenced = receipts.SelectMany(receipt => receipt.PreviousFiles.Select(payload =>
                    Resolve(payload.Path + "." + receipt.Generation + ".old")))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var receipt in receipts)
            {
                if (receipt.CreatedUtc > now || now - receipt.CreatedUtc < UpdateProbation.Retention) continue;
                foreach (var payload in receipt.PreviousFiles)
                {
                    var backup = Resolve(payload.Path + "." + receipt.Generation + ".old");
                    if (!File.Exists(backup)) continue;
                    try
                    {
                        VerifyFile(backup, payload);
                        File.Delete(backup);
                        deleted++;
                    }
                    catch (Exception error)
                    {
                        if (IsSharingViolation(error)) retryable++;
                        else UpdateProbation.WarnOnce(error);
                    }
                }
                // Once every file is gone, no generation remains that could be re-adopted.
                // Otherwise retain the receipt as a tombstone even when it was consumed.
                if (receipt.PreviousFiles.All(p => !File.Exists(Resolve(p.Path + "." + receipt.Generation + ".old"))))
                    try { File.Delete(ReceiptPath(receipt.Generation)); } catch (Exception error) { UpdateProbation.WarnOnce(error); }
            }
            // Old updaters can leave incomplete generations that cannot become rollback
            // receipts. Their cleanup must not depend on rollback being available.
            foreach (var backup in EnumeratePayloadFiles(skipUntrusted: true).Where(path => path.EndsWith(".old", StringComparison.OrdinalIgnoreCase)))
            {
                if (referenced.Contains(backup)) continue;
                var generation = ParseBackup(Path.GetRelativePath(_root, backup))?.Generation;
                // A damaged receipt still identifies a generation requiring local review.
                if (generation != null && File.Exists(ReceiptPath(generation) + ".corrupt")) continue;
                try { RequireTrusted(backup); File.Delete(backup); deleted++; }
                catch (Exception error)
                {
                    if (IsSharingViolation(error)) retryable++;
                    else UpdateProbation.WarnOnce(error);
                }
            }
            CleanupRollbackStages(now);
            return (deleted, EnumeratePayloadFiles(skipUntrusted: true).Count(p => p.EndsWith(".old", StringComparison.OrdinalIgnoreCase)), retryable);
        }
        catch (Exception error)
        {
            if (IsSharingViolation(error)) retryable++;
            else UpdateProbation.WarnOnce(error);
            try { return (deleted, EnumeratePayloadFiles(skipUntrusted: true).Count(p => p.EndsWith(".old", StringComparison.OrdinalIgnoreCase)), retryable); }
            catch { return (deleted, 1, retryable); }
        }
    }

    private static bool IsSharingViolation(Exception error) => error is IOException && (error.HResult & 0xffff) is 32 or 33;

    /// <summary>Consumes the receipt before the first payload mutation. Failure restores this build.</summary>
    internal void Rollback(string generation, DateTime now, Action<string, string>? copy = null)
    {
        if (!Guid.TryParseExact(generation, "N", out _)) throw new InvalidDataException("Invalid recovery generation");
        RequireTrusted(_root);
        using var operationLock = AcquireMutationLock();
        var receipt = ReadExact(generation);
        if (receipt == null)
        {
            receipt = DiscoverLegacy(now);
            if (receipt?.Generation != generation) throw new InvalidDataException("The selected previous build is no longer available");
            SaveVerified(receipt);
        }
        if (!IsCurrent(receipt) || receipt.Consumed || !IsWithinProbation(receipt, now))
            throw new InvalidDataException("Rollback is no longer eligible");
        VerifyPayloads(receipt);
        foreach (var payload in receipt.InstalledFiles) VerifyFile(Resolve(payload.Path), payload);
        var installed = receipt.InstalledFiles.Select(p => p.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var previous in receipt.PreviousFiles)
            if (!installed.Contains(previous.Path) && File.Exists(Resolve(previous.Path)))
                throw new IOException("A new local file occupies a previous-build path");

        // A second process may have consumed it while this process was hashing.
        var currentReceipt = ReadExact(generation);
        if (currentReceipt == null || currentReceipt.Consumed || currentReceipt != receipt &&
            JsonSerializer.Serialize(currentReceipt, JsonOptions) != JsonSerializer.Serialize(receipt, JsonOptions))
            throw new InvalidDataException("Recovery evidence changed");
        var stage = Path.Combine(Metadata, "rollback-" + Guid.NewGuid().ToString("N"));
        var previousStage = Path.Combine(stage, "previous");
        var failedStage = Path.Combine(stage, "current");
        _mkdir(previousStage);
        _mkdir(failedStage);
        foreach (var payload in receipt.PreviousFiles)
        {
            var destination = Path.Combine(previousStage, payload.Path);
            _mkdir(Path.GetDirectoryName(destination)!);
            File.Copy(Resolve(payload.Path + "." + generation + ".old"), destination, false);
            VerifyFile(destination, payload);
        }
        foreach (var payload in receipt.InstalledFiles)
            _mkdir(Path.GetDirectoryName(Path.Combine(failedStage, payload.Path))!);
        _write(Path.Combine(stage, ".retain-for-repair"), "restore in progress");
        SaveVerified(receipt with { Consumed = true });

        var moved = new List<(string Original, string Backup)>();
        var copied = new List<string>();
        try
        {
            foreach (var payload in receipt.InstalledFiles)
            {
                var original = Resolve(payload.Path);
                VerifyFile(original, payload);
                var backup = Path.Combine(failedStage, payload.Path);
                File.Move(original, backup, false);
                moved.Add((original, backup));
            }
            foreach (var payload in receipt.PreviousFiles)
            {
                var original = Resolve(payload.Path);
                _mkdir(Path.GetDirectoryName(original)!);
                if (File.Exists(original)) throw new IOException("Rollback destination changed");
                copied.Add(original);
                if (copy == null) File.Copy(Path.Combine(previousStage, payload.Path), original, false);
                else copy(Path.Combine(previousStage, payload.Path), original);
                VerifyFile(original, payload);
            }
            // A mapped helper executable may keep its staged copy locked until exit;
            // the next cleanup pass removes completed stages without waiting 24 hours.
            try { _write(Path.Combine(stage, ".complete"), "verified"); }
            catch (Exception error) { UpdateProbation.WarnOnce(error); }
            TryDeleteRollbackStage(stage);
        }
        catch
        {
            var failures = new List<string>();
            UpdateChecker.RollBackUpdateFiles(copied, moved, failures.Add);
            if (failures.Count > 0)
            {
                throw new IOException("Rollback failed and could not restore every current file. Retained recovery files require manual review.");
            }
            try { File.Delete(Path.Combine(stage, ".retain-for-repair")); }
            catch (Exception error) { UpdateProbation.WarnOnce(error); }
            CleanupRollbackStages(now);
            throw;
        }
    }

    private void CleanupRollbackStages(DateTime now)
    {
        try
        {
            if (!Directory.Exists(Metadata)) return;
            RequireTrusted(Metadata);
            var stages = Directory.EnumerateDirectories(Metadata, "rollback-*", SearchOption.TopDirectoryOnly)
                .Where(path => Guid.TryParseExact(Path.GetFileName(path)[9..], "N", out _))
                .OrderByDescending(Directory.GetLastWriteTimeUtc).ToArray();
            var keptFailed = false;
            foreach (var stage in stages)
            {
                var completed = File.Exists(Path.Combine(stage, ".complete"));
                if (!completed && File.Exists(Path.Combine(stage, ".retain-for-repair"))) continue;
                var expired = now - Directory.GetLastWriteTimeUtc(stage) >= UpdateProbation.Retention;
                if (!completed && !expired && !keptFailed) { keptFailed = true; continue; }
                TryDeleteRollbackStage(stage);
            }
        }
        catch (Exception error) { UpdateProbation.WarnOnce(error); }
    }

    private void TryDeleteRollbackStage(string stage)
    {
        try
        {
            // Validate the whole tree before deletion; never follow an untrusted link.
            RequireTrusted(stage);
            var files = new List<string>();
            var directories = new List<string> { stage };
            for (var index = 0; index < directories.Count; index++)
            foreach (var entry in Directory.EnumerateFileSystemEntries(directories[index]))
            {
                RequireTrusted(entry);
                if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0) directories.Add(entry);
                else files.Add(entry);
            }
            foreach (var file in files.OrderBy(path => Path.GetFileName(path).StartsWith('.'))) File.Delete(file);
            foreach (var directory in directories.AsEnumerable().Reverse()) Directory.Delete(directory);
        }
        catch (Exception error) { UpdateProbation.WarnOnce(error); }
    }

    private Receipt? DiscoverLegacy(DateTime now)
    {
        var groups = EnumeratePayloadFiles().Select(path => ParseBackup(Path.GetRelativePath(_root, path)))
            .Where(item => item != null).Select(item => item!.Value).GroupBy(item => item.Generation, StringComparer.OrdinalIgnoreCase);
        var candidates = new List<Receipt>();
        var possibleBuilds = 0;
        foreach (var group in groups)
        {
            // Even a damaged/consumed record is evidence that this generation was already considered.
            if (File.Exists(ReceiptPath(group.Key)) || File.Exists(ReceiptPath(group.Key) + ".corrupt")) continue;
            var names = group.Select(item => item.Original).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (names.Contains("GlDrive.exe") || names.Contains("GlDrive.dll")) possibleBuilds++;
            if (!new[] { "GlDrive.exe", "GlDrive.dll", "GlDrive.deps.json", "GlDrive.runtimeconfig.json" }.All(names.Contains)) continue;
            var oldDll = Resolve("GlDrive.dll." + group.Key + ".old");
            RequireTrusted(oldDll);
            var version = _version(oldDll);
            if (version == null || UpdateChecker.Normalize(version) >= _current) continue;
            if (!LegacyDependenciesPresent(group.Key, names)) continue;
            candidates.Add(Capture(group.Key, names, now));
        }
        // Legacy updaters did not record order or complete manifests. Never guess an older
        // generation when the immediately previous one may be damaged or ambiguous.
        return candidates.Count == 1 && possibleBuilds == 1 ? candidates[0] : null;
    }

    private bool LegacyDependenciesPresent(string generation, HashSet<string> files)
    {
        foreach (var name in files.Where(p => p.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)))
        {
            var path = Resolve(name + "." + generation + ".old");
            RequireTrusted(path);
            if (new FileInfo(path).Length > MaximumReceiptBytes) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Object) return false;
            var prefix = Path.GetDirectoryName(name);
            foreach (var target in targets.EnumerateObject())
            foreach (var library in target.Value.EnumerateObject())
            foreach (var kind in new[] { "runtime", "native" })
            {
                if (!library.Value.TryGetProperty(kind, out var assets)) continue;
                foreach (var asset in assets.EnumerateObject())
                {
                    if (asset.Name.EndsWith("/_._", StringComparison.Ordinal)) continue;
                    var file = Path.Combine(prefix ?? "", Path.GetFileName(asset.Name.Replace('/', Path.DirectorySeparatorChar)));
                    var exact = Path.Combine(prefix ?? "", asset.Name.Replace('/', Path.DirectorySeparatorChar));
                    if (!files.Contains(file) && !files.Contains(exact)) return false;
                }
            }
        }
        return true;
    }

    private Receipt Capture(string generation, IEnumerable<string> previousPaths, DateTime now)
    {
        var previous = previousPaths.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
            .Select(path => Hash(Resolve(path + "." + generation + ".old"), path)).ToArray();
        var installed = EnumeratePayloadFiles().Where(path => !path.EndsWith(".old", StringComparison.OrdinalIgnoreCase))
            .Select(path => Hash(path, Path.GetRelativePath(_root, path))).OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        var older = _version(Resolve("GlDrive.dll." + generation + ".old"));
        var newer = _version(Resolve("GlDrive.dll"));
        if (older == null || newer == null || UpdateChecker.Normalize(older) >= UpdateChecker.Normalize(newer))
            throw new InvalidDataException("The previous build is not older than the installed build");
        var receipt = new Receipt(1, generation, UpdateChecker.Normalize(older).ToString(), UpdateChecker.Normalize(newer).ToString(),
            now.ToUniversalTime(), false, previous, installed);
        ValidateReceipt(receipt);
        return receipt;
    }

    private List<Receipt> ReadReceipts()
    {
        if (!Directory.Exists(Metadata)) return [];
        RequireTrusted(Metadata);
        // Bound resident receipt state, evicting oldest observations rather than refusing new
        // evidence. Unselected files remain tombstones and cannot be re-adopted as legacy.
        var newest = new PriorityQueue<string, long>();
        foreach (var file in Directory.EnumerateFiles(Metadata, "*.json", SearchOption.TopDirectoryOnly))
        {
            newest.Enqueue(file, File.GetLastWriteTimeUtc(file).Ticks);
            if (newest.Count > MaximumReceipts) newest.Dequeue();
        }
        var result = new List<Receipt>();
        foreach (var file in newest.UnorderedItems.Select(item => item.Element))
        {
            var receipt = ReadReceiptFile(file);
            if (receipt != null) result.Add(receipt);
        }
        return result;
    }

    private Receipt? ReadExact(string generation) => ReadReceiptFile(ReceiptPath(generation));
    private Receipt? ReadReceiptFile(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            RequireTrusted(file);
            if (new FileInfo(file).Length > MaximumReceiptBytes) throw new InvalidDataException("Recovery receipt exceeds its size bound");
            var receipt = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(file), JsonOptions) ?? throw new InvalidDataException("Empty recovery receipt");
            ValidateReceipt(receipt);
            if (!Path.GetFileNameWithoutExtension(file).Equals(receipt.Generation, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Recovery generation does not match its receipt");
            return receipt;
        }
        catch (Exception error)
        {
            try { if (!File.Exists(file + ".corrupt")) File.Copy(file, file + ".corrupt", false); } catch { }
            UpdateProbation.WarnOnce(error);
            return null;
        }
    }

    internal FileStream AcquireMutationLock()
    {
        RequireTrusted(_root);
        var path = Path.Combine(_root, UpdateProbation.MutationLockFile);
        if (!File.Exists(path)) _write(path, "");
        RequireTrusted(path);
        return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
    private string ReceiptPath(string generation) => Path.Combine(Metadata, generation + ".json");
    private void SaveVerified(Receipt receipt)
    {
        ValidateReceipt(receipt);
        _mkdir(Metadata);
        if (File.Exists(ReceiptPath(receipt.Generation)))
        {
            var previous = ReadExact(receipt.Generation) ?? throw new IOException("Existing recovery evidence cannot be replaced");
            if (previous.Consumed && !receipt.Consumed) throw new IOException("A consumed recovery receipt cannot be reset");
        }
        var text = JsonSerializer.Serialize(receipt, JsonOptions);
        _write(ReceiptPath(receipt.Generation), text);
        RequireTrusted(ReceiptPath(receipt.Generation));
        if (File.ReadAllText(ReceiptPath(receipt.Generation)) != text) throw new IOException("Recovery receipt readback failed");
    }

    private void ValidateReceipt(Receipt receipt)
    {
        if (receipt.Format != 1 || !Guid.TryParseExact(receipt.Generation, "N", out _) ||
            !Version.TryParse(receipt.PreviousVersion, out var previous) || !Version.TryParse(receipt.CurrentVersion, out var current) ||
            UpdateChecker.Normalize(previous) >= UpdateChecker.Normalize(current) || receipt.CreatedUtc.Kind != DateTimeKind.Utc ||
            receipt.PreviousFiles == null || receipt.InstalledFiles == null)
            throw new InvalidDataException("Invalid recovery receipt");
        foreach (var files in new[] { receipt.PreviousFiles, receipt.InstalledFiles })
        {
            if (files.Length is < 1 or > MaximumFiles || files.Select(p => p.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Length)
                throw new InvalidDataException("Invalid recovery file manifest");
            foreach (var payload in files)
            {
                Resolve(payload.Path);
                if (payload.Length < 0 || payload.Sha256.Length != 64 || !payload.Sha256.All(Uri.IsHexDigit) ||
                    payload.Path.EndsWith(".old", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid recovery file entry");
            }
            if (!files.Any(p => p.Path.Equals("GlDrive.exe", StringComparison.OrdinalIgnoreCase)) ||
                !files.Any(p => p.Path.Equals("GlDrive.dll", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Recovery manifest is missing the application");
        }
    }

    private void VerifyPayloads(Receipt receipt)
    {
        foreach (var payload in receipt.PreviousFiles) VerifyFile(Resolve(payload.Path + "." + receipt.Generation + ".old"), payload);
    }

    private Payload Hash(string path, string relative)
    {
        RequireTrusted(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new(relative, stream.Length, Convert.ToHexString(SHA256.HashData(stream)));
    }

    private void VerifyFile(string path, Payload payload)
    {
        var actual = Hash(path, payload.Path);
        if (actual.Length != payload.Length || !actual.Sha256.Equals(payload.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("An installed recovery file changed or is incomplete");
    }

    private List<string> EnumeratePayloadFiles(bool skipUntrusted = false)
    {
        var result = new List<string>();
        var pending = new Stack<string>();
        pending.Push(_root);
        while (pending.TryPop(out var directory))
        {
            string[] entries;
            try { RequireTrusted(directory); entries = Directory.GetFileSystemEntries(directory); }
            catch (Exception error) when (skipUntrusted) { UpdateProbation.WarnOnce(error); continue; }
            foreach (var path in entries)
            {
                if (UpdateProbation.IsMetadataPath(_root, path)) continue;
                try
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        if (skipUntrusted) continue;
                        throw new IOException("An installation path is a link");
                    }
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                    else { RequireTrusted(path); result.Add(path); }
                }
                catch (Exception error) when (skipUntrusted) { UpdateProbation.WarnOnce(error); }
                if (result.Count + pending.Count > MaximumFiles * 4) throw new IOException("Installation exceeds recovery scan bound");
            }
        }
        return result;
    }

    private string Resolve(string relative)
    {
        if (string.IsNullOrEmpty(relative) || relative.Length > 4096 || Path.IsPathRooted(relative) || relative.Contains(':') ||
            relative.Split(['/', '\\']).Any(part => part.Length == 0 || part is "." or ".." || part != part.TrimEnd(' ', '.') || part.Any(char.IsControl)))
            throw new InvalidDataException("Invalid relative recovery path");
        var full = Path.GetFullPath(Path.Combine(_root, relative));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || UpdateProbation.IsMetadataPath(_root, full))
            throw new InvalidDataException("Recovery path escapes the payload root");
        return full;
    }

    private void RequireTrusted(string path)
    {
        if (!_trusted(path)) throw new IOException("Installation or recovery path is not protected from unprivileged writes: " + path);
        for (var cursor = path; cursor != null && cursor.StartsWith(_root, StringComparison.OrdinalIgnoreCase); cursor = Path.GetDirectoryName(cursor))
            if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) throw new IOException("Recovery path contains a link");
    }

    private bool IsCurrent(Receipt receipt) => UpdateChecker.Normalize(Version.Parse(receipt.CurrentVersion)) == _current;
    private static bool IsWithinProbation(Receipt receipt, DateTime now) => receipt.CreatedUtc <= now && now - receipt.CreatedUtc < UpdateProbation.Retention;
    private static UpdateProbationStatus Status(Receipt receipt, bool available, string message) =>
        new(available, receipt.PreviousVersion, receipt.CreatedUtc.Add(UpdateProbation.Retention), message, receipt.Generation);

    private static (string Original, string Generation)? ParseBackup(string relative)
    {
        const int suffixLength = 37; // dot + 32 hex digits + .old
        if (relative.Length <= suffixLength || !relative.EndsWith(".old", StringComparison.OrdinalIgnoreCase) || relative[^suffixLength] != '.') return null;
        var generation = relative.Substring(relative.Length - 36, 32);
        return Guid.TryParseExact(generation, "N", out _) ? (relative[..^suffixLength], generation) : null;
    }
}

internal static class UpdateInstallationTrust
{
    private static readonly HashSet<string> Owners = new(StringComparer.Ordinal)
    {
        "S-1-5-18", "S-1-5-32-544", "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"
    };
    private const int DangerousRights = (int)(FileSystemRights.WriteData | FileSystemRights.AppendData |
        FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes | FileSystemRights.Delete |
        FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership) | 0x50000000;

    internal static bool IsProtected(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var anchors = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }.Where(p => !string.IsNullOrEmpty(p));
            var anchor = anchors.FirstOrDefault(p => full.Equals(p, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(Path.TrimEndingDirectorySeparator(p) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            if (anchor == null) return false;
            for (var cursor = full; cursor != null; cursor = Path.GetDirectoryName(cursor))
            {
                var attributes = File.GetAttributes(cursor);
                if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
                var directory = (attributes & FileAttributes.Directory) != 0;
                FileSystemSecurity security = directory ? new DirectoryInfo(cursor).GetAccessControl() : new FileInfo(cursor).GetAccessControl();
                var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
                // Ancestors above Program Files may permit creation of unrelated siblings; they
                // must still prevent replacement/deletion of this protected anchor.
                var aboveAnchor = cursor.Length < anchor.Length;
                if (!HasProtectedDescriptor(descriptor, aboveAnchor)) return false;
            }
            return true;
        }
        catch { return false; }
    }

    internal static bool HasProtectedDescriptor(RawSecurityDescriptor descriptor, bool ancestorOnly = false)
    {
        if (descriptor.Owner == null || !Owners.Contains(descriptor.Owner.Value) || descriptor.DiscretionaryAcl == null) return false;
        var dangerous = ancestorOnly ? (int)(FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions |
            FileSystemRights.TakeOwnership) | 0x10000000 : DangerousRights;
        foreach (GenericAce ace in descriptor.DiscretionaryAcl)
        {
            if ((ace.AceFlags & AceFlags.InheritOnly) != 0) continue;
            if (ace is not QualifiedAce qualified) return false;
            if (qualified.AceQualifier == AceQualifier.AccessDenied) continue;
            if (qualified.AceQualifier != AceQualifier.AccessAllowed) return false;
            if ((qualified.AccessMask & dangerous) != 0 && !Owners.Contains(qualified.SecurityIdentifier.Value)) return false;
        }
        return true;
    }
}
