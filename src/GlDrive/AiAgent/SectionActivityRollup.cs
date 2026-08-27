using System.IO;
using System.Text.Json;
using Serilog;

namespace GlDrive.AiAgent;

public sealed class SectionActivityRollup : IDisposable
{
    private readonly TelemetryRecorder _recorder;
    private readonly string _aiDataRoot;
    private readonly Timer _timer;

    /// <summary>Race rows the last RollUp could not parse. Non-zero means the section
    /// activity digest for that day under-reports. Mirrors LogDigester.ParseSkips.</summary>
    public int ParseSkips { get; private set; }

    public SectionActivityRollup(TelemetryRecorder recorder, string aiDataRoot)
    {
        _recorder = recorder;
        _aiDataRoot = aiDataRoot;

        // Fire at midnight + 2 min jitter to avoid competing with other midnight jobs
        var nextMidnight = DateTime.Today.AddDays(1) - DateTime.Now + TimeSpan.FromMinutes(2);
        _timer = new Timer(_ => RollUp(DateTime.Today.AddDays(-1)),
            null, nextMidnight, TimeSpan.FromDays(1));
    }

    /// <summary>Aggregate races-{forDate}.jsonl and emit one SectionActivityEvent per (server, section).</summary>
    public void RollUp(DateTime forDate)
    {
        try
        {
            // Reset before the early return: a missing file must clear the previous run's
            // count, not leave yesterday's damage reported against today.
            ParseSkips = 0;
            var racesFile = Path.Combine(_aiDataRoot, $"races-{forDate:yyyyMMdd}.jsonl");
            if (!File.Exists(racesFile)) return;

            var parseSkips = 0;
            var agg = new Dictionary<(string server, string section), SectionActivityEvent>();
            foreach (var line in File.ReadLines(racesFile))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                RaceOutcomeEvent? r;
                try { r = JsonSerializer.Deserialize<RaceOutcomeEvent>(line); }
                catch (Exception ex)
                {
                    // Count and report: this rollup is the agent's only view of per-section
                    // activity, so a silently-dropped race under-reports a section forever.
                    // Warning, not Debug — the Serilog sink runs at Information.
                    parseSkips++;
                    Log.Warning(ex, "SectionActivityRollup parse skip in {File}", racesFile);
                    continue;
                }
                if (r is null) continue;

                foreach (var p in r.Participants)
                {
                    var key = (p.ServerId, r.Section);
                    if (!agg.TryGetValue(key, out var cur))
                    {
                        cur = new SectionActivityEvent
                        {
                            ServerId = p.ServerId,
                            Section = r.Section,
                            DayOfWeek = (int)forDate.DayOfWeek
                        };
                    }
                    cur = cur with
                    {
                        FilesIn  = cur.FilesIn  + p.Files,
                        BytesIn  = cur.BytesIn  + p.Bytes,
                        OurRaces = cur.OurRaces + 1,
                        OurWins  = cur.OurWins  + (r.Winner == p.ServerId ? 1 : 0)
                    };
                    agg[key] = cur;
                }
            }

            ParseSkips = parseSkips;
            if (parseSkips > 0)
                Log.Warning("SectionActivityRollup: {Skips} unparseable race row(s) in {File} — " +
                    "section activity for {Date} is under-reported", parseSkips, racesFile, forDate);

            foreach (var ev in agg.Values)
                _recorder.Record(TelemetryStream.SectionActivity, ev);
        }
        catch (Exception ex) { Log.Warning(ex, "SectionActivityRollup failed for {Date}", forDate); }
    }

    public void Dispose() => _timer.Dispose();
}
