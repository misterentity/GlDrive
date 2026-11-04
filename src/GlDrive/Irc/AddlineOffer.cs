using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GlDrive.Config;
using GlDrive.Util;
using Serilog;

namespace GlDrive.Irc;

/// <summary>An addline you sent to someone (so they can add you on their site). Kept so the
/// login can be filled in when they reply with the site's details.</summary>
public sealed record AddlineOffer(string IrcServerId, string Nick, string Username, string Password,
    IReadOnlyList<string> Masks, DateTimeOffset SentAtUtc)
{
    public string Line => AddlineText.Build(Username, Password, Masks);
}

public static class AddlineText
{
    private const string PasswordAlphabet = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>The line glftpd siteops expect: <c>site adduser user pass ident@ip ...</c>.</summary>
    public static string Build(string username, string password, IEnumerable<string> masks)
    {
        var parts = new List<string> { "site adduser", username.Trim(), password.Trim() };
        parts.AddRange(masks.Select(m => m.Trim()).Where(m => m.Length > 0));
        return string.Join(' ', parts);
    }

    /// <summary>A password with no ambiguous characters and nothing glftpd or IRC treats specially.</summary>
    public static string GeneratePassword(int length = 16) =>
        new(Enumerable.Range(0, length).Select(_ => PasswordAlphabet[RandomNumberGenerator.GetInt32(PasswordAlphabet.Length)]).ToArray());

    /// <summary>Splits "*@1.2.3.4, ident@5.6.7.*" into masks; a bare IP becomes *@ip.</summary>
    public static IReadOnlyList<string> ParseMasks(string? text) =>
        (text ?? "").Split([' ', ',', ';', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(m => m.Contains('@') ? m : "*@" + m).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Usernames/passwords glftpd accepts on one line: no spaces or control characters.</summary>
    public static bool IsSafeToken(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 64 && value.All(c => c > ' ' && c != 0x7F);
}

/// <summary>
/// Sent addlines, DPAPI-encrypted (they hold passwords), kept 60 days. Remembers the last
/// username and masks so the next addline starts filled in.
/// </summary>
public sealed class AddlineOfferStore
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(60);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string _path;
    private readonly Lock _gate = new();

    private sealed class Data
    {
        public List<AddlineOffer> Offers { get; set; } = [];
        public string LastUsername { get; set; } = "";
        public List<string> LastMasks { get; set; } = [];
    }

    public AddlineOfferStore() : this(Path.Combine(ConfigManager.AppDataPath, "addline-offers.json")) { }
    internal AddlineOfferStore(string path) => _path = path;

    public (string Username, IReadOnlyList<string> Masks) LastUsed()
    {
        lock (_gate) { var d = Load(); return (d.LastUsername, d.LastMasks); }
    }

    public void Record(AddlineOffer offer)
    {
        lock (_gate)
        {
            var d = Load();
            d.Offers.RemoveAll(o => SameNick(o, offer.IrcServerId, offer.Nick));
            d.Offers.Add(offer);
            d.LastUsername = offer.Username;
            d.LastMasks = offer.Masks.ToList();
            Save(d);
        }
    }

    /// <summary>The latest addline sent to this nick on this IRC network, if any.</summary>
    public AddlineOffer? Find(string ircServerId, string nick)
    {
        lock (_gate) return Load().Offers.Where(o => SameNick(o, ircServerId, nick)).MaxBy(o => o.SentAtUtc);
    }

    private static bool SameNick(AddlineOffer o, string serverId, string nick) =>
        o.IrcServerId == serverId && string.Equals(o.Nick, nick, StringComparison.OrdinalIgnoreCase);

    private Data Load()
    {
        try
        {
            if (!File.Exists(_path)) return new();
            var json = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(_path), null, DataProtectionScope.CurrentUser));
            var data = JsonSerializer.Deserialize<Data>(json, Json) ?? new();
            data.Offers.RemoveAll(o => DateTimeOffset.UtcNow - o.SentAtUtc > Retention);
            return data;
        }
        catch (Exception ex)
        {
            Log.Warning("Addline offers unreadable ({Error}); starting empty", ex.GetType().Name);
            return new();
        }
    }

    private void Save(Data data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data, Json)), null, DataProtectionScope.CurrentUser);
        SecureFile.WriteAllBytesRestricted(_path, bytes);
    }
}
