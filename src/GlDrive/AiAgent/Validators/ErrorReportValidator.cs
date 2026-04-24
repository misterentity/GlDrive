using System.IO;
using GlDrive.Config;

namespace GlDrive.AiAgent;

public sealed class ErrorReportValidator : IChangeValidator
{
    public string Category => AgentCategories.ErrorReport;

    private readonly string _issuesDir;

    public ErrorReportValidator(string aiDataRoot)
    {
        _issuesDir = Path.Combine(aiDataRoot, "ai-briefs", "issues");
        Directory.CreateDirectory(_issuesDir);
    }

    public ValidationResult Validate(AgentChange change, AppConfig config)
    {
        // target: "issues/<sig>"  (informational — no config mutation)
        const string prefix = "issues/";
        if (!change.Target.StartsWith(prefix))
            return new(false, "target-shape-unsupported", null);
        var sig = change.Target[prefix.Length..];
        if (string.IsNullOrWhiteSpace(sig))
            return new(false, "missing-sig", null);
        if (change.After is null)
            return new(false, "after-null", null);

        var content = change.After.ToString() ?? "";
        var issuesDirLocal = _issuesDir;

        return new(true, null, _ =>
        {
            var filename = $"{DateTime.Now:yyyyMMdd}-{Sanitize(sig)}.md";
            var path = Path.Combine(issuesDirLocal, filename);
            File.WriteAllText(path, content);
        });
    }

    private static string Sanitize(string s)
        => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-'));
}
