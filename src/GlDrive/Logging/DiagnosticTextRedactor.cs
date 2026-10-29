using System.Diagnostics;
using System.IO;
using System.Text;

namespace GlDrive.Logging;

/// <summary>Replace values in the original input only; replacement markers are never rescanned.</summary>
internal static class DiagnosticTextRedactor
{
    internal const int MaxInputChars = 1024 * 1024;
    internal const int MaxOutputChars = 2 * 1024 * 1024;
    internal static Func<string, string> Create(IEnumerable<string> secretValues, Func<string, string>? protocol = null, bool throwOnInventoryFailure = false)
    {
        var secrets = new HashSet<string>(StringComparer.Ordinal);
        var chars = 0;
        try
        {
            foreach (var secret in secretValues)
            {
                if (string.IsNullOrEmpty(secret) || !secrets.Add(secret)) continue;
                if (secret.Length > 65536 || secrets.Count > 4096 || (chars += secret.Length) > 256 * 1024)
                    throw new InvalidDataException("Diagnostic redaction inventory exceeds its bound");
            }
        }
        catch { if (throwOnInventoryFailure) throw; return _ => ""; }
        var ordered = secrets.OrderByDescending(s => s.Length).ToArray();
        // Even a one-character credential must not appear inside our chosen marker.
        var mask = new[] { "[redacted]", "[hidden]", "█", "" }.First(m => !ordered.Any(m.Contains));
        return input =>
        {
            try
            {
                if (input.Length > MaxInputChars) return mask;
                var watch = Stopwatch.StartNew();
                var original = protocol?.Invoke(input) ?? input;
                if (original.Length > MaxOutputChars) return mask;
                // A protocol mask can split a known multi-word credential. Preserve
                // coverage of that original value by masking the whole entry, rather
                // than trying to find only its remaining suffix in transformed text.
                if (!string.Equals(original, input, StringComparison.Ordinal))
                    foreach (var secret in ordered)
                    {
                        if (watch.Elapsed > TimeSpan.FromMilliseconds(250)) return mask;
                        if (Occurrences(input, secret, watch) != Occurrences(original, secret, watch)) return mask;
                    }
                var result = new StringBuilder(Math.Min(original.Length, 65536));
                var offset = 0;
                while (offset < original.Length)
                {
                    if (watch.Elapsed > TimeSpan.FromMilliseconds(250)) return mask;
                    var next = original.Length; var length = 0; var examined = 0;
                    foreach (var secret in ordered)
                    {
                        if (++examined % 32 == 0 && watch.Elapsed > TimeSpan.FromMilliseconds(250)) return mask;
                        var found = original.IndexOf(secret, offset, StringComparison.Ordinal);
                        if (found < 0 || found >= next) continue;
                        next = found; length = secret.Length;
                        if (found == offset) break; // longest-first resolves overlapping values.
                    }
                    if (result.Length + next - offset + (length > 0 ? mask.Length : 0) > MaxOutputChars) return mask;
                    result.Append(original, offset, next - offset);
                    if (length == 0) break;
                    result.Append(mask); offset = next + length;
                }
                var safe = result.ToString();
                foreach (var secret in ordered)
                {
                    if (watch.Elapsed > TimeSpan.FromMilliseconds(250) || safe.Contains(secret, StringComparison.Ordinal)) return mask;
                }
                return safe;
            }
            catch { return mask; }
        };
    }

    private static int Occurrences(string input, string value, Stopwatch watch)
    {
        var count = 0; var offset = 0;
        while (offset < input.Length)
        {
            if (count % 128 == 0 && watch.Elapsed > TimeSpan.FromMilliseconds(250)) throw new TimeoutException();
            var found = input.IndexOf(value, offset, StringComparison.Ordinal);
            if (found < 0) break;
            count++; offset = found + value.Length;
        }
        return count;
    }
}
