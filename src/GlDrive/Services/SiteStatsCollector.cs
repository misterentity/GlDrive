using System.Text.RegularExpressions;
using FluentFTP;
using Serilog;

namespace GlDrive.Services;

/// <summary>
/// Runs `SITE STATS` on a borrowed FTP connection and scrapes credits + ratio
/// out of the multi-line reply. glftpd themes vary, so we try several common
/// shapes and accept whichever matches first.
/// </summary>
public static class SiteStatsCollector
{
    // Size unit pattern: K/M/G/T optionally followed by B/iB (KiB/MiB/GiB/TiB common in glftpd themes).
    // Bare K/M/G/T also accepted; "B" alone (raw bytes) intentionally excluded.
    private const string SizeUnit = @"([KMGT](?:i?B)?)";

    // Examples this matches:
    //   "Credits: 12.3 GB"   "Credits: 12.3 GiB"   "Cr 12.3GB"   "CR(12.3GB)"
    private static readonly Regex CreditsLabelFirst =
        new(@"(?i)\b(?:credits?|cr)\b\s*[:=()|\s]+([\d.,]+)\s*" + SizeUnit + @"\b",
            RegexOptions.Compiled);
    private static readonly Regex CreditsValueFirst =
        new(@"(?i)\b([\d.,]+)\s*" + SizeUnit + @"\b\s*(?:credits?|cr)\b",
            RegexOptions.Compiled);

    // Examples this matches:
    //   "Ratio: 1:3"   "ratio=UL"   "Ratios Unlimited - - - -"   "ratio: inf"
    private static readonly Regex RatioRe =
        new(@"(?i)\bratios?\b\s*[:=]?\s*(\d+\s*:\s*\d+|unlimited|UL|inf)",
            RegexOptions.Compiled);

    // glftpd ACL denial returns this phrase as the only body line — treat as a parse miss
    // so the candidate-chain falls through to the next command.
    private static readonly Regex AccessDeniedRe =
        new(@"(?i)you do not have access to this command", RegexOptions.Compiled);

    public static async Task<SiteStats> RefreshAsync(AsyncFtpClient client, string command, CancellationToken ct)
    {
        var reply = await client.Execute(command, ct);
        // FluentFTP gives us the multi-line response in InfoMessages; fall back to ErrorMessage.
        var body = (reply.InfoMessages ?? string.Empty) + "\n" + (reply.Message ?? string.Empty);
        Log.Information("SiteStatsCollector: cmd={Cmd} success={Success} bodyLen={Len} body={Body}",
            command, reply.Success, body.Length,
            body.Length > 600 ? body[..600] + "...(truncated)" : body);
        return Parse(body);
    }

    public static SiteStats Parse(string body)
    {
        // glftpd ACL denial — let the candidate chain keep trying.
        if (AccessDeniedRe.IsMatch(body))
        {
            Log.Information("SiteStatsCollector: ACL-denied response, skipping candidate");
            return new SiteStats(null, null, DateTime.UtcNow);
        }

        string? ratio = null;
        var ratioMatch = RatioRe.Match(body);
        if (ratioMatch.Success)
        {
            var r = Regex.Replace(ratioMatch.Groups[1].Value, @"\s+", "");
            ratio = r.Equals("unlimited", StringComparison.OrdinalIgnoreCase) ? "UL" : r;
        }

        string? credits = null;
        // Skip parsing credits when ratio is unlimited: glftpd shows "Credits: 0.0GiB ..."
        // for leech accounts, which is meaningless and looks like a bug to the user.
        if (ratio != "UL")
        {
            var creditsMatch = CreditsLabelFirst.Match(body);
            if (!creditsMatch.Success) creditsMatch = CreditsValueFirst.Match(body);
            if (creditsMatch.Success)
            {
                var num = creditsMatch.Groups[1].Value.Replace(",", "");
                var unit = creditsMatch.Groups[2].Value.ToUpperInvariant().Replace("I", "");
                if (unit.Length == 1) unit += "B"; // bare K/M/G/T → KB/MB/GB/TB
                credits = $"{num}{unit}";
            }
        }

        Log.Information("SiteStatsCollector: parsed credits={Credits} ratio={Ratio}",
            credits ?? "(null)", ratio ?? "(null)");

        return new SiteStats(credits, ratio, DateTime.UtcNow);
    }
}
