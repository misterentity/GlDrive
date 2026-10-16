namespace GlDrive.Config;

/// <summary>Supported download capacities shared by the editor, automation and lane planner.</summary>
public static class ConfigurationLimits
{
    public const int MinConcurrentDownloads = 1;
    public const int MaxConcurrentDownloads = 5;
    public const int DefaultConcurrentDownloads = 1;
    public const int MinHttpLanesPerNetwork = 1;
    public const int MaxHttpLanesPerNetwork = 32;
    public const int DefaultHttpLanesPerNetwork = 4;

    public static int ParseConcurrentDownloads(string? text) => int.TryParse(text, out var value)
        ? Math.Clamp(value, MinConcurrentDownloads, MaxConcurrentDownloads)
        : DefaultConcurrentDownloads;

    public static int ParseHttpLanesPerNetwork(string? text) => int.TryParse(text, out var value)
        ? ClampHttpLanesPerNetwork(value)
        : DefaultHttpLanesPerNetwork;

    public static int ClampHttpLanesPerNetwork(int value) =>
        Math.Clamp(value, MinHttpLanesPerNetwork, MaxHttpLanesPerNetwork);
}
