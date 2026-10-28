using GlDrive.Config;

namespace GlDrive.UI;

internal static class IrcChannelEditor
{
    internal static List<IrcChannelConfig> Parse(string? text, IReadOnlyList<IrcChannelConfig> previous) => (text ?? "")
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(line =>
        {
            var autoJoin = !line.StartsWith('-');
            var parts = (autoJoin ? line : line[1..]).Split(' ', 2);
            return new IrcChannelConfig
            {
                Name = parts[0], Key = parts.Length > 1 ? parts[1] : "", AutoJoin = autoJoin,
                Sitebot = previous.FirstOrDefault(c => c.Name.Equals(parts[0], StringComparison.OrdinalIgnoreCase))?.Sitebot ?? new()
            };
        }).ToList();
}
