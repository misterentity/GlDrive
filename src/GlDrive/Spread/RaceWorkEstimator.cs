using GlDrive.Downloads;

namespace GlDrive.Spread;

internal sealed record RaceWorkEstimate(long? ExpectedBytes, double PredictedSeconds, string Basis);

/// <summary>Advisory queue cost from existing observations. No listing, probing or pool access.</summary>
internal static class RaceWorkEstimator
{
    internal const double MinimumSeconds = 5;
    internal const double MaximumSeconds = 5400;
    internal const double DefaultSeconds = 300;
    internal const double DefaultBytesPerSecond = 10 * 1024 * 1024;
    private const long MaximumBytes = 1L << 50;
    private const double MaximumBytesPerSecond = 1L << 40;

    internal static RaceWorkEstimate Estimate(string section, string releaseName, long? announcedBytes,
        double routeBytesPerSecond, IEnumerable<RaceHistoryItem> history)
    {
        var resolution = Resolution(releaseName);
        var samples = history.Take(5000).Where(h => h != null && h.Result == SpreadJobState.Completed &&
                h.CompletedAt > h.StartedAt && (h.CompletedAt - h.StartedAt).TotalHours <= 24 &&
                h.BytesTransferred >= 0 && h.BytesTransferred <= MaximumBytes)
            .Select(h => new Sample(h.Section, Resolution(h.ReleaseName),
                (h.CompletedAt - h.StartedAt).TotalSeconds, h.BytesTransferred))
            .ToArray();
        var cohort = samples.Where(s => string.Equals(s.Section, section, StringComparison.OrdinalIgnoreCase) && s.Resolution == resolution).ToArray();
        var basis = "section-resolution";
        if (cohort.Length == 0) { cohort = samples.Where(s => string.Equals(s.Section, section, StringComparison.OrdinalIgnoreCase)).ToArray(); basis = "section"; }
        if (cohort.Length == 0) { cohort = samples.Where(s => s.Resolution == resolution).ToArray(); basis = "resolution"; }
        if (cohort.Length == 0) { cohort = samples; basis = "global"; }

        var exactBytes = ValidBytes(announcedBytes);
        var historicBytes = Median(cohort.Where(s => s.Bytes > 0).Select(s => (double)s.Bytes));
        var expectedBytes = exactBytes ?? (historicBytes.HasValue ? (long?)historicBytes.Value : null);
        var historicSpeed = Median(cohort.Where(s => s.Bytes > 0).Select(s => s.Bytes / s.Seconds).Where(ValidSpeed));
        var routeSpeed = ValidSpeed(routeBytesPerSecond) ? routeBytesPerSecond : (double?)null;

        if (exactBytes.HasValue)
        {
            var speed = routeSpeed ?? historicSpeed ?? DefaultBytesPerSecond;
            return new(expectedBytes, ClampSeconds(exactBytes.Value / speed),
                "announced-size/" + (routeSpeed.HasValue ? "route-speed" : historicSpeed.HasValue ? basis + "-speed" : "default-speed"));
        }
        if (expectedBytes.HasValue && routeSpeed.HasValue)
            return new(expectedBytes, ClampSeconds(expectedBytes.Value / routeSpeed.Value), basis + "-size/route-speed");
        var duration = Median(cohort.Select(s => s.Seconds));
        return new(expectedBytes, ClampSeconds(duration), duration.HasValue ? basis + "-duration" : "default-duration");
    }

    internal static double ClampSeconds(double? seconds) => seconds.HasValue && double.IsFinite(seconds.Value) && seconds.Value > 0
        ? Math.Clamp(seconds.Value, MinimumSeconds, MaximumSeconds) : DefaultSeconds;

    private static QualityProfile Resolution(string? release) => string.IsNullOrEmpty(release) || release.Length > 1024
        ? QualityProfile.Any : ReleaseTraits.Parse(release).Resolution;
    private static long? ValidBytes(long? bytes) => bytes is > 0 and <= MaximumBytes ? bytes : null;
    private static bool ValidSpeed(double speed) => double.IsFinite(speed) && speed > 0 && speed <= MaximumBytesPerSecond;
    private static double? Median(IEnumerable<double> values)
    {
        var sorted = values.Where(double.IsFinite).Order().ToArray();
        if (sorted.Length == 0) return null;
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : sorted[middle - 1] / 2 + sorted[middle] / 2;
    }
    private sealed record Sample(string Section, QualityProfile Resolution, double Seconds, long Bytes);
}
