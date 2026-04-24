namespace GlDrive.AiAgent;

public sealed class OverridesDigester
{
    public OverridesDigest Build(IEnumerable<ConfigOverrideEvent> events)
    {
        var list = events.ToList();
        var d = new OverridesDigest();
        d.Paths = list.Select(e => e.JsonPointer).Distinct().Take(200).ToList();
        d.RevertedAiPaths = list.Where(e => !string.IsNullOrEmpty(e.AiAuditRef))
                                .Select(e => e.JsonPointer).Distinct().Take(100).ToList();
        return d;
    }
}
