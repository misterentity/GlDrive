namespace GlDrive.Irc;

internal static class FishKeyCommand
{
    internal static bool TryParse(string arguments, string? selectedTarget, out string target, out string key)
    {
        var parts = arguments.Split(' ', 2);
        target = parts.Length == 2 ? parts[0] : selectedTarget ?? "";
        key = parts.Length == 2 ? parts[1] : parts[0];
        return !string.IsNullOrWhiteSpace(target) && !string.IsNullOrWhiteSpace(key);
    }
}
