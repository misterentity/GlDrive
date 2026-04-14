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
    public List<IrcChannelConfig> Channels { get; set; } = [];
    public List<IrcAnnounceRule> AnnounceRules { get; set; } = [];
    public RequestFillerConfig RequestFiller { get; set; } = new();
}

/// <summary>
/// RaceTrade-style auto request filler: detects request announces in IRC
/// and fills them by racing a matching release from another connected server
/// into this server's request path.
/// </summary>
public class RequestFillerConfig
{
    public bool Enabled { get; set; }
    /// <summary>Regex matching request announces. Must capture named group "release".</summary>
    public string Pattern { get; set; } = @"!request\s+(?<release>\S+)";
    /// <summary>IRC channel to monitor (empty = all channels).</summary>
    public string Channel { get; set; } = "";
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
