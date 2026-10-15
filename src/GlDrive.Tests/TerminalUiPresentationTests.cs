using System.Globalization;
using System.Windows;
using GlDrive.Config;
using GlDrive.Downloads;
using GlDrive.Services;
using GlDrive.UI;
using Xunit;

namespace GlDrive.Tests;

/// <summary>Behaviour behind the 3.11.12 terminal UI pass: projections and helpers, no WPF window.</summary>
public sealed class TerminalUiPresentationTests
{
    // ── Release-name tokens ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Sinners.2026.2160p.UHD.BluRay.x265-LIGHTBRiNGER")]
    [InlineData("Sinners.2026.PROPER.2160p.UHD.BluRay.DV.HDR.x265-NCmt")]
    [InlineData("The.Outer.Worlds.S03E04.1080p.WEB-DL.H264")]
    [InlineData("Aphex_Twin-Selected_Algorithm_Works-WEB-2026-FALCON")]
    [InlineData("Hades.III.Update.v1.0.4-RAZOR1911")]
    [InlineData("名前.2026.1080p.WEB.H264-グループ")]
    [InlineData("Émilie..Café  2026 -- 1080p_-_x264-")]
    [InlineData("🎬.Movie.2026.1080p.WEB-GRP")]
    [InlineData("-")]
    [InlineData("---")]
    [InlineData("...")]
    [InlineData("-GROUP")]
    [InlineData("SingleWord")]
    [InlineData("tab\there.2026-GRP")]
    [InlineData(" leading.and.trailing ")]
    public void Tokens_always_rebuild_the_exact_original_text(string name)
    {
        var tokens = ReleaseNameTokenizer.Tokenize(name);
        Assert.Equal(name, string.Concat(tokens.Select(t => t.Text)));
        Assert.All(tokens, t => Assert.False(string.IsNullOrEmpty(t.Text)));
    }

    [Fact]
    public void Empty_or_missing_names_produce_no_tokens()
    {
        Assert.Empty(ReleaseNameTokenizer.Tokenize(null));
        Assert.Empty(ReleaseNameTokenizer.Tokenize(""));
    }

    [Fact]
    public void Scene_names_are_split_into_title_metadata_and_group()
    {
        var tokens = Words("Sinners.2026.PROPER.2160p.UHD.BluRay.DV.HDR.x265-NCmt");
        var expected = new List<(string, ReleaseTokenKind)>
        {
            ("Sinners", ReleaseTokenKind.Title), ("2026", ReleaseTokenKind.Year), ("PROPER", ReleaseTokenKind.Tag),
            ("2160p", ReleaseTokenKind.Resolution), ("UHD", ReleaseTokenKind.Source), ("BluRay", ReleaseTokenKind.Source),
            ("DV", ReleaseTokenKind.Codec), ("HDR", ReleaseTokenKind.Codec), ("x265", ReleaseTokenKind.Codec),
            ("NCmt", ReleaseTokenKind.Group)
        };
        Assert.Equal(expected, tokens);
    }

    [Fact]
    public void Web_dl_is_a_source_not_a_group_and_episodes_end_the_title()
    {
        var tokens = Words("The.Outer.Worlds.S03E04.1080p.WEB-DL");
        Assert.Equal(("The", ReleaseTokenKind.Title), tokens[0]);
        Assert.Equal(("Worlds", ReleaseTokenKind.Title), tokens[2]);
        Assert.Equal(("S03E04", ReleaseTokenKind.Episode), tokens[3]);
        Assert.Equal(("DL", ReleaseTokenKind.Source), tokens[^1]);
        Assert.DoesNotContain(tokens, t => t.Kind == ReleaseTokenKind.Group);
    }

    [Theory]
    [InlineData("1917.2019.1080p.BluRay.x264-GRP", "1917")]
    [InlineData("The.Final.Countdown.1980.1080p.BluRay.x264-GRP", "Countdown")]
    [InlineData("DC.League.of.Super-Pets.2022.1080p.WEB.H264-GRP", "League")]
    public void Title_words_that_look_like_metadata_stay_in_the_title(string name, string word)
    {
        Assert.Contains((word, ReleaseTokenKind.Title), Words(name));
        Assert.Equal(ReleaseTokenKind.Title, Words(name)[0].Kind);
    }

    [Fact]
    public void Names_without_metadata_keep_full_contrast_title_text()
    {
        // Nothing recognisable: the whole name must not fall back to dim text.
        Assert.All(Words("Some.Random.Folder.Name"), t => Assert.Equal(ReleaseTokenKind.Title, t.Kind));
    }

    [Fact]
    public void Music_releases_tokenize_without_losing_their_group()
    {
        var tokens = Words("Aphex_Twin-Selected_Algorithm_Works-WEB-2026-FALCON");
        Assert.Equal(("Aphex", ReleaseTokenKind.Title), tokens[0]);
        Assert.Contains(("WEB", ReleaseTokenKind.Source), tokens);
        Assert.Contains(("2026", ReleaseTokenKind.Year), tokens);
        Assert.Equal(("FALCON", ReleaseTokenKind.Group), tokens[^1]);
    }

    [Fact]
    public void Every_token_kind_maps_to_a_theme_brush_or_inherits_the_title_colour()
    {
        Assert.Null(ReleaseNameTokenizer.BrushKey(ReleaseTokenKind.Title));
        foreach (var kind in Enum.GetValues<ReleaseTokenKind>().Where(k => k != ReleaseTokenKind.Title))
            Assert.StartsWith("ReleaseToken", ReleaseNameTokenizer.BrushKey(kind));
    }

    private static List<(string Text, ReleaseTokenKind Kind)> Words(string name) =>
        ReleaseNameTokenizer.Tokenize(name).Where(t => t.Kind != ReleaseTokenKind.Separator)
            .Select(t => (t.Text, t.Kind)).ToList();

    // ── Transfer state presentation ────────────────────────────────────────────────────────

    [Fact]
    public void Each_download_state_has_its_own_glyph()
    {
        var states = Enum.GetNames<DownloadStatus>();
        var glyphs = states.Select(TransferStatePresentation.Glyph).ToList();
        Assert.Equal(glyphs.Count, glyphs.Distinct().Count());
        Assert.DoesNotContain("·", glyphs);
        Assert.Equal("·", TransferStatePresentation.Glyph("SomethingNew"));
        Assert.Equal("·", TransferStatePresentation.Glyph(null));
    }

    [Fact]
    public void Live_race_failed_and_finished_races_are_distinguishable_without_colour()
    {
        var glyphs = new[] { "Running", "Paused", "Completed", "Failed", "Stopped" }.Select(TransferStatePresentation.Glyph).ToList();
        Assert.Equal(glyphs.Count, glyphs.Distinct().Count());
        Assert.NotEqual(TransferStatePresentation.Glyph("Running"), TransferStatePresentation.Glyph("Downloading"));
    }

    // ── Live download progress (UI projection only) ─────────────────────────────────────────

    [Fact]
    public void Live_progress_moves_the_bar_without_rebuilding_the_row()
    {
        var item = new DownloadItem { Status = DownloadStatus.Downloading };
        var row = new DownloadItemVm { Status = "Downloading" };
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        row.ApplyProgress(item, new DownloadProgress(10, 100, 5, "r00"));
        Assert.Equal(10, row.ProgressPercent, 3);
        row.ApplyProgress(item, new DownloadProgress(60, 100, 5, "r01"));
        Assert.Equal(60, row.ProgressPercent, 3);
        Assert.Equal("60% — r01", row.ProgressText);
        Assert.Equal(2, changed.Count(n => n == nameof(DownloadItemVm.ProgressPercent)));
    }

    [Theory]
    [InlineData(50, 0, 0)]
    [InlineData(50, -1, 0)]
    [InlineData(150, 100, 100)]
    [InlineData(-5, 100, 0)]
    public void Progress_handles_unknown_totals_and_clamps(long downloaded, long total, double expected)
    {
        var item = new DownloadItem { Status = DownloadStatus.Downloading };
        var row = new DownloadItemVm { Status = "Downloading", ProgressPercent = 42 };
        row.ApplyProgress(item, new DownloadProgress(downloaded, total, 0));
        Assert.Equal(expected, row.ProgressPercent, 3);
        Assert.InRange(row.ProgressPercent, 0, 100);
    }

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    [InlineData(double.NegativeInfinity, 0)]
    [InlineData(-12.5, 0)]
    [InlineData(140, 100)]
    [InlineData(37.5, 37.5)]
    public void Progress_percent_is_always_a_valid_bar_value(double assigned, double expected)
    {
        // RefreshDownloads assigns ProgressPercent directly; a ProgressBar must never receive NaN or out-of-range values.
        var row = new DownloadItemVm { ProgressPercent = 50 };
        var changed = 0;
        row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(DownloadItemVm.ProgressPercent)) changed++; };
        row.ProgressPercent = assigned;
        Assert.Equal(expected, row.ProgressPercent, 6);
        Assert.Equal(1, changed);
    }

    [Theory]
    [InlineData(DownloadStatus.Queued)]
    [InlineData(DownloadStatus.Extracting)]
    [InlineData(DownloadStatus.Completed)]
    [InlineData(DownloadStatus.Failed)]
    [InlineData(DownloadStatus.Cancelled)]
    public void Stale_progress_after_leaving_downloading_does_not_move_the_bar(DownloadStatus status)
    {
        var item = new DownloadItem { Status = DownloadStatus.Downloading };
        var row = new DownloadItemVm { Status = "Downloading" };
        row.ApplyProgress(item, new DownloadProgress(30, 100, 5));
        item.Status = status;
        row.ApplyProgress(item, new DownloadProgress(95, 100, 5));
        Assert.Equal(30, row.ProgressPercent, 3);
        Assert.Equal("30%", row.ProgressText);
    }

    // ── Search filter chips ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Quality_and_source_filters_are_exclusive_and_toggle_off()
    {
        var s = new SearchFilterToggle.State("", "", false);
        s = SearchFilterToggle.Apply(s, "quality:1080p");
        Assert.Equal("1080p", s.Quality);
        s = SearchFilterToggle.Apply(s, "quality:2160p");
        Assert.Equal("2160p", s.Quality);
        s = SearchFilterToggle.Apply(s, "source:WEB");
        s = SearchFilterToggle.Apply(s, "source:BLURAY");
        Assert.Equal(("2160p", "BLURAY"), (s.Quality, s.Source));
        s = SearchFilterToggle.Apply(s, "quality:2160p");
        Assert.Equal("", s.Quality);
        Assert.True(s.AnyActive);
    }

    [Fact]
    public void Size_toggles_and_clear_resets_every_filter()
    {
        var s = SearchFilterToggle.Apply(new("1080p", "WEB", false), "size:1GB");
        Assert.True(s.BigSize);
        s = SearchFilterToggle.Apply(s, "clear");
        Assert.Equal(new SearchFilterToggle.State("", "", false), s);
        Assert.False(s.AnyActive);
    }

    [Theory]
    [InlineData("", "", false, "Movie.2026.2160p.WEB.H265-GRP", 1L, true)]
    [InlineData("1080p", "", false, "Movie.2026.1080P.WEB.H264-GRP", 1L, true)]     // case-insensitive, as before
    [InlineData("1080p", "", false, "Movie.2026.2160p.WEB.H265-GRP", 1L, false)]
    [InlineData("", "BLURAY", false, "Movie.2026.1080p.BluRay.x264-GRP", 1L, true)]
    [InlineData("", "WEB", false, "Movie.2026.1080p.BluRay.x264-GRP", 1L, false)]
    [InlineData("", "", true, "Movie.2026.1080p.WEB-GRP", (1L << 30) - 1, false)]
    [InlineData("", "", true, "Movie.2026.1080p.WEB-GRP", 1L << 30, true)]
    [InlineData("1080p", "WEB", true, "Movie.2026.1080p.WEB-GRP", 2L << 30, true)]
    public void Search_rows_match_every_active_filter(string quality, string source, bool big, string name, long size, bool visible)
    {
        Assert.Equal(visible, SearchFilterToggle.Matches(new(quality, source, big), name, size));
    }

    [Fact]
    public void Search_rows_without_a_name_only_pass_an_empty_filter()
    {
        Assert.True(SearchFilterToggle.Matches(new("", "", false), null, 0));
        Assert.False(SearchFilterToggle.Matches(new("1080p", "", false), null, 0));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("quality")]
    [InlineData("unknown:value")]
    public void Malformed_filter_arguments_change_nothing(string? arg)
    {
        var s = new SearchFilterToggle.State("1080p", "WEB", true);
        Assert.Equal(s, SearchFilterToggle.Apply(s, arg));
    }

    // ── Stream matrix auto-expand ───────────────────────────────────────────────────────────

    [Fact]
    public void Stream_matrix_opens_when_a_transfer_starts_but_respects_a_manual_collapse()
    {
        var latch = new AutoExpandLatch();
        Assert.True(latch.SignalChanged(true));      // multi-stream transfer starts → open
        Assert.False(latch.SignalChanged(true));     // repeated progress ticks never re-open
        latch.UserToggled(false);                    // user closes it
        Assert.False(latch.SignalChanged(true));
        Assert.False(latch.SignalChanged(false));    // transfer ends
        Assert.True(latch.SignalChanged(true));      // next transfer opens again
        latch.UserToggled(false);
        latch.UserToggled(true);                     // user reopens: a later restart still opens
        latch.SignalChanged(false);
        Assert.True(latch.SignalChanged(true));
    }

    // ── Status strip ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Status_strip_never_shows_empty_or_trailing_separators()
    {
        Assert.Equal("", StatusSegmentsConverter.Join(["", null, "  "]));
        Assert.Equal("Idle  //  0 active", StatusSegmentsConverter.Join(["Idle", "0 active", "", null]));
        Assert.Equal("IRC ○ off", StatusSegmentsConverter.Join([null, "", "IRC ○ off"]));
        var converted = new StatusSegmentsConverter().Convert(
            ["Idle", DependencyProperty.UnsetValue, "site: 1/4"], typeof(string), null!, CultureInfo.InvariantCulture);
        Assert.Equal("Idle  //  site: 1/4", converted);
    }

    [Theory]
    [InlineData("OFFLINE", "OFFLINE", false)]
    [InlineData("offline ", "OFFLINE", false)]
    [InlineData("", "MOUNTED", false)]
    [InlineData("CONNECTED", "PRIMARY", true)]
    public void Server_status_line_is_hidden_when_it_repeats_the_chip(string line, string tag, bool visible)
    {
        var result = new DistinctTextVisibilityConverter().Convert([line, tag], typeof(Visibility), null!, CultureInfo.InvariantCulture);
        Assert.Equal(visible ? Visibility.Visible : Visibility.Collapsed, result);
    }

    // ── Sparkline ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Sparkline_peak_reads_the_highest_plotted_sample()
    {
        Assert.Equal(0, TelemetrySparkline.PeakFraction(null, 40));
        Assert.Equal(0, TelemetrySparkline.PeakFraction(Array.Empty<Point>(), 40));
        Point[] points = [new(0, 40), new(1, 10), new(2, 30)];
        Assert.Equal(0.75, TelemetrySparkline.PeakFraction(points, 40), 6);
        Assert.Equal(1, TelemetrySparkline.PeakFraction([new Point(0, -5)], 40));
    }

    // ── MCP connection callout ──────────────────────────────────────────────────────────────

    [Fact]
    public void Mcp_callout_starts_off_when_disabled_and_unknown_when_enabled()
    {
        Assert.Equal(McpConnectionState.Off, NewMcp(enabled: false).ConnectionState);
        Assert.Equal(McpConnectionState.Unknown, NewMcp(enabled: true).ConnectionState);
    }

    [Fact]
    public void Mcp_callout_follows_probe_results()
    {
        var vm = NewMcp(enabled: true);
        vm.ApplyProbeResult(new McpProbeResult(true, "Local API connected", 10, 11));
        Assert.Equal(McpConnectionState.Connected, vm.ConnectionState);
        Assert.Contains("CONNECTED", vm.ConnectionStateLabel);
        vm.ApplyProbeResult(new McpProbeResult(false, "Cannot reach the local API."));
        Assert.Equal(McpConnectionState.Error, vm.ConnectionState);
        Assert.Equal("Cannot reach the local API.", vm.Status);
        Assert.StartsWith("✕", vm.ConnectionStateLabel);

        var disabled = NewMcp(enabled: false);
        disabled.ApplyProbeResult(new McpProbeResult(false, "MCP / local control API is disabled."));
        Assert.Equal(McpConnectionState.Off, disabled.ConnectionState);
    }

    [Fact]
    public void Mcp_panel_has_at_most_one_primary_action()
    {
        var vm = NewMcp(enabled: false);
        Assert.False(vm.HasPendingChanges);
        Assert.Equal(!vm.AdapterMissing, vm.CopyIsPrimary);

        vm.Enabled = true;                           // unsaved edit: Apply becomes the primary action
        Assert.True(vm.HasPendingChanges);
        Assert.False(vm.CopyIsPrimary);
        vm.Enabled = false;
        vm.Port = "not a port";
        Assert.True(vm.HasPendingChanges);
        Assert.False(vm.CopyIsPrimary);
    }

    private static McpSettingsViewModel NewMcp(bool enabled) =>
        new(new AppConfig { ControlApi = new() { Enabled = enabled, Port = 8756 } }, readLocalState: false);
}
