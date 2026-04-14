using System.Text;
using System.Windows;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Spread;

namespace GlDrive.UI;

public partial class RuleTestDialog : Window
{
    private readonly SiteSpreadConfig _site;
    private readonly SkiplistEvaluator _evaluator = new();

    public RuleTestDialog(SiteSpreadConfig site)
    {
        _site = site;
        InitializeComponent();
    }

    private void Evaluate_Click(object sender, RoutedEventArgs e)
    {
        var release = ReleaseBox.Text?.Trim() ?? "";
        var ircSection = SectionBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(release))
        {
            ResultBox.Text = "Enter a release name.";
            return;
        }

        var parsed = SceneNameParser.Parse(release);
        var sb = new StringBuilder();
        sb.AppendLine($"Release:  {release}");
        sb.AppendLine($"Parsed:   title={parsed.Title}, year={parsed.Year?.ToString() ?? "-"}, " +
                      $"group={parsed.Group ?? "-"}, quality={parsed.Quality}, source={parsed.Source ?? "-"}, " +
                      $"season={parsed.Season?.ToString() ?? "-"}, episode={parsed.Episode?.ToString() ?? "-"}");
        sb.AppendLine(new string('-', 70));

        // 1. Section mapping
        var mapping = SectionMapper.Resolve(_site, ircSection, release);
        if (mapping.HasValue)
        {
            sb.AppendLine($"SECTION MAPPING: {ircSection} → {mapping.Value.RemoteSection}");
            sb.AppendLine($"  Tag rules: {mapping.Value.TagRules.Count}");
        }
        else if (_site.SectionMappings.Count > 0)
        {
            sb.AppendLine($"SECTION MAPPING: none matched for [{ircSection}]");
        }
        else
        {
            sb.AppendLine("SECTION MAPPING: (none configured)");
        }
        sb.AppendLine();

        // 2. Affiliate check
        if (parsed.Group is { } g && _site.Affils.Any(a =>
                string.Equals(a, g, StringComparison.OrdinalIgnoreCase)))
        {
            sb.AppendLine($"AFFILIATE: group '{g}' is in affils → ALLOW (highest priority)");
            ResultBox.Text = sb.ToString();
            return;
        }

        // 3. Full tiered evaluation with trace
        var effectiveSection = mapping?.RemoteSection ?? ircSection;
        var tagRules = mapping?.TagRules ?? (IReadOnlyList<SkiplistRule>)Array.Empty<SkiplistRule>();

        var action = _evaluator.EvaluateTiered(release, isDir: true, inRace: false,
            effectiveSection, _site.Skiplist, tagRules, Array.Empty<SkiplistRule>(),
            _site.Affils, parsed);

        sb.AppendLine($"FINAL ACTION: {action}");
        sb.AppendLine(new string('-', 70));

        // 4. Per-rule trace (site rules only — tag rules aren't in the EvaluateWithTrace path)
        var (traceAction, trace) = _evaluator.EvaluateWithTrace(release, true, false,
            effectiveSection, _site.Skiplist, Array.Empty<SkiplistRule>(), parsed);

        sb.AppendLine("SITE RULES TRACE:");
        foreach (var t in trace)
        {
            var marker = t.IsMatch ? "[MATCH]" : "       ";
            sb.AppendLine($"  {marker} {t.Pattern,-40} → {t.Result}");
        }

        if (tagRules.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("TAG RULES TRACE:");
            var (_, tagTrace) = _evaluator.EvaluateWithTrace(release, true, false,
                effectiveSection, tagRules, Array.Empty<SkiplistRule>(), parsed);
            foreach (var t in tagTrace)
            {
                var marker = t.IsMatch ? "[MATCH]" : "       ";
                sb.AppendLine($"  {marker} {t.Pattern,-40} → {t.Result}");
            }
        }

        ResultBox.Text = sb.ToString();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
