namespace GlDrive.Config;

public enum FishMode { ECB, CBC }

public class IrcConfig
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 6697;
    public bool UseTls { get; set; } = true;
    public string Nick { get; set; } = "";
    public string AltNick { get; set; } = "";
    public string RealName { get; set; } = "GlDrive";
    public bool AutoConnect { get; set; }
    public string InviteNick { get; set; } = "";
    public bool FishEnabled { get; set; }
    public FishMode FishMode { get; set; } = FishMode.CBC;
    /// <summary>
    /// Legacy 8-bit charset (e.g. "windows-1251", "windows-1252") for FiSH peers whose client
    /// does NOT use UTF-8. Applied only as a fallback when a decrypt is not valid UTF-8 but looks
    /// like text. Empty = UTF-8 only (decode as UTF-8, the modern default).
    /// </summary>
    public string FallbackCharset { get; set; } = "";
    public List<IrcChannelConfig> Channels { get; set; } = [];
    public List<IrcAnnounceRule> AnnounceRules { get; set; } = [];
    public RequestFillerConfig RequestFiller { get; set; } = new();
}

/// <summary>
/// Auto request filler: detects open requests from the sitebot's announce and the
/// site's requests directory, and fills them by racing a matching release from
/// another connected server into <c>RequestPath/DirectoryPrefix+release/release</c>.
/// </summary>
public class RequestFillerConfig
{
    public bool Enabled { get; set; }
    /// <summary>
    /// Regex matching the sitebot's request announce. Must capture named group "release";
    /// "number" and "nick" are optional. Empty (or the old "!request" default) uses the built-in pattern.
    /// </summary>
    public string Pattern { get; set; } = GlDrive.Spread.RequestAnnounceParser.DefaultRequestedPattern;
    /// <summary>Regex matching the sitebot's "request filled" announce (named group "release"). Empty = built-in.</summary>
    public string FilledPattern { get; set; } = "";
    /// <summary>IRC channel to monitor and to send the filled command to (empty = all channels).</summary>
    public string Channel { get; set; } = "";
    /// <summary>Only accept request announces from this nick (empty = any nick).</summary>
    public string BotNick { get; set; } = "";
    /// <summary>The site's requests directory.</summary>
    public string RequestPath { get; set; } = "/requests";
    /// <summary>Prefix of an open request's directory name.</summary>
    public string DirectoryPrefix { get; set; } = "REQUEST-";
    /// <summary>
    /// Sent once a fill is complete. {release} and {number} are substituted. A value
    /// starting with "SITE " goes over FTP; anything else is said in the IRC channel.
    /// </summary>
    public string FilledCommand { get; set; } = "!reqfilled {release}";
    /// <summary>Max number of request fills per hour (rate limit).</summary>
    public int MaxPerHour { get; set; } = 10;
    /// <summary>Minimum seconds between request fills.</summary>
    public int CooldownSeconds { get; set; } = 60;
}

public class IrcChannelConfig
{
    public string Name { get; set; } = "";
    public string Key { get; set; } = "";
    public bool AutoJoin { get; set; } = true;
    public GlDrive.Spread.SitebotChannelConfig Sitebot { get; set; } = new();
}

/// <summary>
/// Rule for detecting new releases from IRC channel announces.
/// The Pattern is a regex applied to channel messages. Named groups
/// "section" and "release" extract the section name and release name.
/// </summary>
public class IrcAnnounceRule
{
    public bool Enabled { get; set; } = true;
    public string Channel { get; set; } = "";
    public string Pattern { get; set; } = "";
    public bool AutoRace { get; set; } = true;
}
