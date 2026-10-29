using GlDrive.Services.Operations;

namespace GlDrive.Spread;

public partial class SpreadManager
{
    internal (IReadOnlyList<DossierEvent> Events, bool Partial) ReadDossierEvents(string release)
    {
        lock (_lock)
        {
            var rows = new List<DossierEvent>();
            var partial = _raceQueue.Count > 1000;
            string Text(string? value)
            { if (value is { Length: <= 1024 }) return value; partial = true; return "[oversized or unavailable field omitted]"; }
            foreach (var request in _raceQueue.Take(1000))
                if (string.Equals(request.ReleaseName, release, StringComparison.OrdinalIgnoreCase))
                    rows.Add(new(new DateTimeOffset(request.QueuedAtUtc), "race-queue", "",
                        $"Queued in {Text(request.Section)}; {Text(request.WaitReason ?? "waiting for admission")}; resumes {request.ResumeCount}."));
            var history = _history.ReadSnapshot(1001);
            partial |= history.Count > 1000;
            foreach (var item in history.Take(1000))
                if (string.Equals(item.ReleaseName, release, StringComparison.OrdinalIgnoreCase))
                    rows.Add(new(new DateTimeOffset(item.CompletedAt), "race-history", "",
                        $"{item.Result}; stop {Text(item.StopReason)}; clean {(item.CleanComplete ? "true" : item.Result == SpreadJobState.Completed && item.FilesTotal <= 0 ? "unknown" : "false")}; {item.FilesDelivered}/{item.FilesTotal} files; {Text(item.FailureCategory)}."));
            return (rows, partial);
        }
    }
}
