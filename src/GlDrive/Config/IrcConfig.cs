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
