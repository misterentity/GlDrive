using System.IO;
using FluentFTP.Exceptions;
using GlDrive.Config;
using GlDrive.Spread;

namespace GlDrive.Downloads;

internal enum RelocationOutcome { NotApplicable, Relocated, Waiting, Missing, Exhausted }
internal sealed record RelocationResult(RelocationOutcome Outcome, string Reason, TimeSpan Delay = default);

/// <summary>Bounded, persisted recovery for release folders moved after an announce.</summary>
internal sealed class DownloadRelocation(Func<ServerConfig> server,
    Func<string, CancellationToken, Task<bool>> directoryExists)
{
    internal const int MaxAttempts = 3;
    internal static readonly TimeSpan LookupInterval = TimeSpan.FromMinutes(5);

    internal static bool IsMissingRemotePath(Exception error)
    {
        for (Exception? current = error; current != null; current = current.InnerException)
        {
            if (current is FileNotFoundException && current.Message.StartsWith("Remote path does not exist", StringComparison.Ordinal))
                return true;
            if (current is FtpCommandException ftp && ftp.CompletionCode == "550" &&
                (ftp.Message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                 ftp.Message.Contains("no such", StringComparison.OrdinalIgnoreCase) ||
                 ftp.Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
    }

    internal static IReadOnlyList<string> Candidates(ServerConfig server, DownloadItem item)
    {
        if (!SafeSegment(item.Category) || !SafeSegment(item.ReleaseName)) return [];
        var paths = new List<string>();
        void Add(string? folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || !folder.StartsWith('/') || folder.Contains('\\') ||
                folder.Any(char.IsControl) || folder.Split('/').Any(p => p is "." or "..")) return;
            var path = folder.TrimEnd('/') + "/" + item.ReleaseName;
            if (!path.Equals(item.RemotePath.TrimEnd('/'), StringComparison.Ordinal) && !paths.Contains(path, StringComparer.Ordinal))
                paths.Add(path);
        }
        Add(server.Notifications.WatchPath.TrimEnd('/') + "/" + item.Category);
        // Check the category's mapped folder first, then configured section roots.
        // These are exact directory-existence checks, never recursive listings.
        foreach (var mapping in server.SpreadSite.SectionMappings)
        {
            if (SectionMapper.Resolve(new SiteSpreadConfig { SectionMappings = [mapping] }, item.Category, item.ReleaseName) is not { } mapped)
                continue;
            Add(mapping.Path);
            Add(server.SpreadSite.Sections.FirstOrDefault(p => p.Key.Equals(mapped.RemoteSection, StringComparison.OrdinalIgnoreCase)).Value);
            Add(server.Notifications.WatchPath.TrimEnd('/') + "/" + mapped.RemoteSection);
        }
        foreach (var path in server.SpreadSite.Sections.Values) Add(path);
        return paths;
    }

    internal static RelocationResult? Admission(DownloadItem item, DateTime now)
    {
        if (item.SourceKind != DownloadSourceKind.Ftp || !SafeSegment(item.ReleaseName) ||
            !SafeSegment(item.Category) || !item.RemotePath.TrimEnd('/').EndsWith("/" + item.ReleaseName, StringComparison.Ordinal))
            return new(RelocationOutcome.NotApplicable, "Not a named release directory");
        if (item.RelocationAttempts >= MaxAttempts)
            return new(RelocationOutcome.Exhausted, $"Release relocation lookup budget exhausted ({MaxAttempts} attempts)");
        if (item.LastRelocationLookupAt is { } last && now < last + LookupInterval)
            return new(RelocationOutcome.Waiting, "Waiting for the release relocation lookup interval", last + LookupInterval - now);
        return null;
    }

    internal async Task<RelocationResult> ResolveAsync(DownloadItem item, DateTime now, Action persist, CancellationToken ct)
    {
        if (Admission(item, now) is { } denied) return denied;
        ct.ThrowIfCancellationRequested();
        item.RelocationAttempts++;
        item.LastRelocationLookupAt = now;
        // Persist before issuing FTP commands so restart cannot reset the probe budget.
        persist();
        if (await directoryExists(item.RemotePath, ct))
            return new(RelocationOutcome.NotApplicable, "The release directory still exists; a child file may be missing");
        foreach (var candidate in Candidates(server(), item))
        {
            ct.ThrowIfCancellationRequested();
            if (!await directoryExists(candidate, ct)) continue;
            ct.ThrowIfCancellationRequested();
            var previous = item.RemotePath;
            item.RemotePath = candidate;
            return new(RelocationOutcome.Relocated, $"relocated from {previous} to {candidate}");
        }
        return new(RelocationOutcome.Missing, $"Release directory missing; relocation lookup {item.RelocationAttempts}/{MaxAttempts} found no matching directory");
    }

    private static bool SafeSegment(string value) => !string.IsNullOrWhiteSpace(value) && !value.StartsWith('.') &&
        !value.Any(c => c is '/' or '\\' || char.IsControl(c));
}
