using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GlDrive.Irc;
using GlDrive.Util;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// DpapiFile replaced three hand-written DPAPI stores. The files already on Dave's disk were
/// written by the old code: DPAPI CurrentUser, no entropy, UTF-8 JSON — camelCase for addline
/// offers and PM history, default (PascalCase) for IRC unread. The fixtures here are literal
/// JSON encrypted exactly the way the old Save methods did, not produced by today's types.
/// </summary>
public sealed class DpapiFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gldrive-dpapi-" + Guid.NewGuid().ToString("N"));

    public DpapiFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string PathOf(string name) => Path.Combine(_dir, name);

    /// <summary>The old stores' write: Protect(UTF8(json), null, CurrentUser).</summary>
    private static void WriteLegacy(string path, string json)
        => File.WriteAllBytes(path, ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser));

    private static JsonElement ReadLegacy(string path)
        => JsonDocument.Parse(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser)).RootElement.Clone();

    // ---- existing on-disk files still load ------------------------------------

    [Fact]
    public void Addline_offers_written_by_the_old_store_still_load()
    {
        var path = PathOf("addline-offers.json");
        var sent = DateTimeOffset.UtcNow.AddDays(-1).ToString("O");
        WriteLegacy(path, $$"""
            {"offers":[{"ircServerId":"irc1","nick":"Bob","username":"dave","password":"pw-fixture",
              "masks":["*@1.2.3.4"],"sentAtUtc":"{{sent}}","line":"site adduser dave pw-fixture *@1.2.3.4"}],
             "lastUsername":"dave","lastMasks":["*@1.2.3.4","ident@5.6.7.*"]}
            """);

        var store = new AddlineOfferStore(path);

        var offer = store.Find("irc1", "bob");
        Assert.NotNull(offer);
        Assert.Equal("pw-fixture", offer!.Password);
        Assert.Equal(["*@1.2.3.4"], offer.Masks);
        var (user, masks) = store.LastUsed();
        Assert.Equal("dave", user);
        Assert.Equal(["*@1.2.3.4", "ident@5.6.7.*"], masks);
    }

    [Fact]
    public void Pm_history_written_by_the_old_store_still_loads()
    {
        var path = PathOf("pm-history-srv.json");
        WriteLegacy(path, """
            {
              "Bob": [
                { "timestamp": "2026-11-01T10:00:00", "nick": "Bob", "text": "hello", "type": 0, "wasEncrypted": true },
                { "timestamp": "2026-11-01T10:01:00", "nick": "me", "text": "hi", "type": 1, "wasEncrypted": false }
              ]
            }
            """);

        using var store = new PmHistoryStore(path, loadNow: true);

        var conversation = store.GetAll()["bob"];
        Assert.Equal(["hello", "hi"], conversation.Select(e => e.Text));
        Assert.True(conversation[0].WasEncrypted);
        Assert.Equal(IrcMessageType.Action, conversation[1].Type);
        Assert.False(File.Exists(path + ".corrupt"));
    }

    [Fact]
    public void Irc_unread_written_by_the_old_store_still_loads()
    {
        var path = PathOf("irc-unread.dat");
        WriteLegacy(path, """
            [{"Target":"#chan","Messages":3,"Highlights":1,"LastUtc":"2026-11-01T10:00:00Z",
              "Generation":"0123456789abcdef0123456789abcdef","Received":5,"ReceivedHighlights":2}]
            """);

        using var store = new IrcUnreadStore(path);

        var row = store.Get("#chan");
        Assert.NotNull(row);
        Assert.Equal(3, row!.Messages);
        Assert.Equal(1, row.Highlights);
        Assert.Equal(5, row.Received);
        Assert.False(File.Exists(path + ".corrupt"));
    }

    // ---- what is written now is the same format -------------------------------

    [Fact]
    public void New_writes_keep_the_old_encryption_and_json_shape()
    {
        var addline = PathOf("addline-offers.json");
        new AddlineOfferStore(addline).Record(new AddlineOffer("irc1", "Bob", "dave", "pw", ["*@1.2.3.4"], DateTimeOffset.UtcNow));
        var offers = ReadLegacy(addline);
        Assert.Equal("dave", offers.GetProperty("lastUsername").GetString());
        Assert.Equal("Bob", offers.GetProperty("offers")[0].GetProperty("nick").GetString());

        var pm = PathOf("pm-history-srv.json");
        using (var store = new PmHistoryStore(pm, loadNow: false))
        {
            store.Append("Bob", new IrcMessageItem { Nick = "Bob", Text = "hello" });
            store.Flush();
        }
        Assert.Equal("hello", ReadLegacy(pm).GetProperty("Bob")[0].GetProperty("text").GetString());

        var unread = PathOf("irc-unread.dat");
        using (var store = new IrcUnreadStore(unread))
        {
            store.Record("#chan", highlight: false, DateTime.UtcNow);
            store.Save();
        }
        Assert.Equal("#chan", ReadLegacy(unread)[0].GetProperty("Target").GetString());

        // Unique staging names, renamed into place: no fixed "<file>.tmp" left behind.
        Assert.Equal(["addline-offers.json", "irc-unread.dat", "pm-history-srv.json"],
            Directory.GetFiles(_dir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    // ---- unreadable files ------------------------------------------------------

    [Fact]
    public void Unreadable_pm_history_and_unread_files_are_kept_as_corrupt()
    {
        var pm = PathOf("pm-history-srv.json");
        File.WriteAllBytes(pm, [1, 2, 3, 4]);
        using (var store = new PmHistoryStore(pm, loadNow: true))
            Assert.Empty(store.GetAll());
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(pm + ".corrupt"));

        var unread = PathOf("irc-unread.dat");
        WriteLegacy(unread, """[{"Target":"#chan","Messages":-1,"Highlights":0,"Generation":"g","Received":0}]""");
        using (var store = new IrcUnreadStore(unread))
            Assert.Empty(store.Snapshot());
        Assert.True(File.Exists(unread + ".corrupt"));
    }

    [Fact]
    public void Unreadable_addline_offers_start_empty_without_a_corrupt_copy()
    {
        var path = PathOf("addline-offers.json");
        File.WriteAllBytes(path, [9, 9, 9]);
        var store = new AddlineOfferStore(path);
        Assert.Equal("", store.LastUsed().Username);
        Assert.False(File.Exists(path + ".corrupt"));
    }

    [Fact]
    public void ReadJson_distinguishes_missing_from_corrupt()
    {
        var path = PathOf("value.json");
        var reports = 0;
        Assert.Null(DpapiFile.ReadJson<int[]>(path, onCorrupt: _ => reports++));
        Assert.Equal(0, reports);
        Assert.False(File.Exists(path + ".corrupt"));

        DpapiFile.WriteJson(path, new[] { 1, 2, 3 });
        Assert.Equal([1, 2, 3], DpapiFile.ReadJson<int[]>(path)!);

        Assert.Null(DpapiFile.ReadJson<int[]>(path, v => v.Length == 0, onCorrupt: _ => reports++));
        Assert.Null(DpapiFile.ReadJson<int[]>(path, maxBytes: 8, onCorrupt: _ => reports++));
        Assert.Equal(2, reports);
        Assert.True(File.Exists(path + ".corrupt"));
        Assert.Equal([1, 2, 3], DpapiFile.ReadJson<int[]>(path)!); // the original is left in place
    }
}
