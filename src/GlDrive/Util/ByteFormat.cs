namespace GlDrive.Util;

/// <summary>
/// 1024-based size and rate text. A call site states the decimals it shows per unit, so one
/// engine keeps each column's existing text ("512 KB" vs "512.0 KB", "1.5 GB" vs "1.50 GB").
/// </summary>
internal static class ByteFormat
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];
    private static readonly string[] IecUnits = ["B", "KiB", "MiB", "GiB", "TiB"];

    /// <summary>"800 B", "512 KB", "12.3 MB", "1.5 GB"; GB is the largest unit.</summary>
    internal static string Compact(long bytes, string? zeroText = null) => Format(bytes, [0, 0, 1, 1], zeroText);

    /// <param name="value">Bytes, or bytes per second with <paramref name="suffix"/> "/s".</param>
    /// <param name="decimals">Decimals for each unit from B upward; its length sets the largest unit (at most TB).</param>
    /// <param name="zeroText">Returned for a value of zero or less; null formats it like any other value ("0 B").</param>
    /// <param name="minUnit">Smallest unit shown (1 = KB, so 500 bytes reads "0 KB").</param>
    /// <param name="iec">KiB/MiB/GiB/TiB labels instead of KB/MB/GB/TB.</param>
    internal static string Format(double value, ReadOnlySpan<int> decimals, string? zeroText = null,
        string suffix = "", int minUnit = 0, bool iec = false)
    {
        if (decimals.Length is 0 or > 5) throw new ArgumentOutOfRangeException(nameof(decimals));
        if (zeroText != null && value <= 0) return zeroText;
        var unit = Math.Clamp(minUnit, 0, decimals.Length - 1);
        var scaled = value / Math.Pow(1024, unit);
        while (scaled >= 1024 && unit < decimals.Length - 1) { scaled /= 1024; unit++; }
        return $"{scaled.ToString("F" + decimals[unit])} {(iec ? IecUnits : Units)[unit]}{suffix}";
    }
}
