using System.IO;
using GlDrive.Irc;
using Xunit;

namespace GlDrive.Tests;

public class IrcScrollbackBufferTests
{
    private static IrcMessageItem Msg(string nick, string text) =>
        new() { Nick = nick, Text = text };

    [Fact]
    public void Preserves_append_order()
    {
        var buf = new IrcScrollbackBuffer();
        buf.Append("#chan", Msg("a", "1"));
        buf.Append("#chan", Msg("b", "2"));
        buf.Append("#chan", Msg("c", "3"));

        var snap = buf.Snapshot("#chan");
        Assert.Equal(new[] { "1", "2", "3" }, snap.Select(m => m.Text));
    }

    [Fact]
    public void Caps_per_target_dropping_oldest()
    {
        var buf = new IrcScrollbackBuffer(maxPerTarget: 3);
        for (var i = 1; i <= 5; i++)
            buf.Append("#chan", Msg("n", i.ToString()));

        Assert.Equal(new[] { "3", "4", "5" }, buf.Snapshot("#chan").Select(m => m.Text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Empty_target_normalizes_to_status_window(string? target)
    {
        var buf = new IrcScrollbackBuffer();
        buf.Append(target, Msg("", "sys"));

        Assert.Equal("*", IrcScrollbackBuffer.NormalizeTarget(target));
        Assert.Single(buf.Snapshot("*"));
    }

    [Fact]
    public void Targets_are_case_insensitive()
    {
        var buf = new IrcScrollbackBuffer();
        buf.Append("#Chan", Msg("a", "1"));
        buf.Append("#chan", Msg("b", "2"));

        Assert.Single(buf.Targets);
        Assert.Equal(2, buf.Snapshot("#CHAN").Count);
    }

    [Fact]
    public void Snapshot_is_isolated_from_later_appends()
    {
        var buf = new IrcScrollbackBuffer();
        buf.Append("#chan", Msg("a", "1"));
        var snap = buf.Snapshot("#chan");
        buf.Append("#chan", Msg("b", "2"));

        Assert.Single(snap);
    }

    [Fact]
    public void Unknown_target_returns_empty()
    {
        var buf = new IrcScrollbackBuffer();
        Assert.Empty(buf.Snapshot("#nothing"));
    }
}

public class IrcLogStoreParsingTests
{
    [Fact]
    public void Parses_valid_line_with_file_date()
    {
        var ok = IrcLogStore.TryParseLine(
            "13:45:12\t#chan\tsomebot\thello world", new DateTime(2026, 7, 18), out var entry);

        Assert.True(ok);
        Assert.Equal(new DateTime(2026, 7, 18, 13, 45, 12), entry.Timestamp);
        Assert.Equal("#chan", entry.Channel);
        Assert.Equal("somebot", entry.Nick);
        Assert.Equal("hello world", entry.Text);
    }

    [Fact]
    public void Text_keeps_embedded_tabs_intact()
    {
        // Sanitize strips tabs on write, but a hand-edited file must not corrupt parsing
        var ok = IrcLogStore.TryParseLine(
            "01:02:03\t#c\tnick\ta\tb\tc", DateTime.Today, out var entry);
        Assert.True(ok);
        Assert.Equal("a\tb\tc", entry.Text);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("13:45\t#chan\tnick\ttext")]          // bad time format
    [InlineData("25:99:99\t#chan\tnick\ttext")]       // out-of-range time
    [InlineData("13:45:12\tnickname\tnick\ttext")]    // PM target — not a channel
    [InlineData("13:45:12\t#chan\tnick")]             // missing text column
    public void Rejects_malformed_lines(string line)
        => Assert.False(IrcLogStore.TryParseLine(line, DateTime.Today, out _));

    [Theory]
    [InlineData("#chan", true)]
    [InlineData("&local", true)]
    [InlineData("!ephemeral", true)]
    [InlineData("+modeless", true)]
    [InlineData("nickname", false)]
    [InlineData("*", false)]
    [InlineData("", false)]
    public void Channel_name_detection(string target, bool expected)
        => Assert.Equal(expected, IrcLogStore.IsChannelName(target));
}

public class PmHistoryStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gldrive-pmtest-" + Guid.NewGuid().ToString("N"));

    private string StorePath => Path.Combine(_dir, "pm-history-test.json");

    private static IrcMessageItem Msg(string nick, string text, bool encrypted = false) =>
        new() { Nick = nick, Text = text, WasEncrypted = encrypted, Type = IrcMessageType.Normal };

    [Fact]
    public void Roundtrips_conversations_encrypted()
    {
        using (var store = new PmHistoryStore(StorePath, loadNow: true))
        {
            store.Append("fReeq", Msg("fReeq", "hi there", encrypted: true));
            store.Append("fReeq", Msg("me", "hello back", encrypted: true));
            store.Append("otherguy", Msg("otherguy", "yo"));
            store.Flush();
        }

        // File on disk must not be plaintext JSON
        var raw = File.ReadAllText(StorePath);
        Assert.DoesNotContain("hello back", raw);

        using var reloaded = new PmHistoryStore(StorePath, loadNow: true);
        var all = reloaded.GetAll();
        Assert.Equal(2, all.Count);
        Assert.Equal(new[] { "hi there", "hello back" }, all["fReeq"].Select(e => e.Text));
        Assert.True(all["freeq"][0].WasEncrypted); // case-insensitive target lookup
    }

    [Fact]
    public void Dispose_flushes_tail_without_explicit_flush()
    {
        // Reproduces the confirmed teardown finding: an appended message must survive
        // Dispose even when Flush() was never called and the 2s debounce never fired.
        using (var store = new PmHistoryStore(StorePath, loadNow: true))
        {
            store.Append("peer", Msg("peer", "last words"));
            // no Flush(), no wait — straight to Dispose
        }

        using var reloaded = new PmHistoryStore(StorePath, loadNow: true);
        Assert.Equal("last words", reloaded.GetAll()["peer"].Single().Text);
    }

    [Fact]
    public void Append_after_dispose_persists_synchronously()
    {
        // Producer-race window: a PM delivered by the read loop after Dispose began
        // must still be persisted (ScheduleSave falls back to a synchronous flush).
        var store = new PmHistoryStore(StorePath, loadNow: true);
        store.Append("peer", Msg("peer", "before"));
        store.Dispose();
        store.Append("peer", Msg("peer", "after-dispose"));

        using var reloaded = new PmHistoryStore(StorePath, loadNow: true);
        Assert.Equal(new[] { "before", "after-dispose" }, reloaded.GetAll()["peer"].Select(e => e.Text));
    }

    [Fact]
    public void Caps_messages_per_target()
    {
        using var store = new PmHistoryStore(StorePath, loadNow: true);
        for (var i = 1; i <= 250; i++)
            store.Append("peer", Msg("peer", i.ToString()));

        var entries = store.GetAll()["peer"];
        Assert.Equal(200, entries.Count);
        Assert.Equal("51", entries[0].Text);   // oldest 50 dropped
        Assert.Equal("250", entries[^1].Text);
    }

    [Fact]
    public void Evicts_stalest_target_at_capacity()
    {
        using var store = new PmHistoryStore(StorePath, loadNow: true);
        var t0 = DateTime.Now.AddHours(-2);
        for (var i = 0; i < 50; i++)
            store.Append($"nick{i}", new IrcMessageItem { Nick = "x", Text = "m", Timestamp = t0.AddMinutes(i) });

        store.Append("newcomer", Msg("newcomer", "fresh"));

        var all = store.GetAll();
        Assert.Equal(50, all.Count);
        Assert.True(all.ContainsKey("newcomer"));
        Assert.False(all.ContainsKey("nick0")); // stalest conversation evicted
    }

    [Fact]
    public void Corrupt_file_starts_empty_and_snapshots()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StorePath, "not dpapi data at all");

        using var store = new PmHistoryStore(StorePath, loadNow: true);
        Assert.Empty(store.GetAll());
        Assert.True(File.Exists(StorePath + ".corrupt"));
    }

    [Fact]
    public void Missing_file_is_fine()
    {
        using var store = new PmHistoryStore(StorePath, loadNow: true);
        Assert.Empty(store.GetAll());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
