using System.IO;
using System.Text;
using System.Xml.Linq;
using Serilog;

namespace GlDrive.Config;

/// <summary>
/// Imports FTP site definitions from FTPRush (RushSite.xml) and FlashFXP (Sites.dat/.ftp).
/// </summary>
public static class SiteImporter
{
    static SiteImporter()
    {
        // Enable legacy encodings (windows-1252, etc.) used by FlashFXP/FTPRush XML exports
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }
    /// <summary>
    /// Auto-detect file format and import sites.
    /// </summary>
    public static List<ServerConfig> ImportAuto(string filePath)
    {
        var ext = Path.GetExtension(filePath);

        if (ext.Equals(".ftp", StringComparison.OrdinalIgnoreCase))
            return ImportFlashFxpXml(filePath);

        if (ext.Equals(".dat", StringComparison.OrdinalIgnoreCase))
            return ImportFlashFxpDat(filePath);

        if (ext.Equals(".xml", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var doc = XDocument.Load(filePath);
                var firstSite = doc.Descendants("SITE").FirstOrDefault();
                if (firstSite?.Element("ADDRESS") != null)
                    return ImportFlashFxpXml(filePath);
            }
            catch { }

            return ImportFtpRush(filePath);
        }

        try { return ImportFtpRush(filePath); }
        catch { return ImportFlashFxpDat(filePath); }
    }

    public static List<ServerConfig> ImportFtpRush(string xmlPath)
    {
        var results = new List<ServerConfig>();
        var doc = XDocument.Load(xmlPath);

        foreach (var site in doc.Descendants("SITE"))
        {
            try
            {
                var host = site.Element("HOST")?.Value?.Trim();
                var user = site.Element("USER")?.Value?.Trim();
                if (string.IsNullOrEmpty(host)) continue;

                var port = 21;
                var portEl = site.Element("PORT");
                if (portEl != null && int.TryParse(portEl.Value, out var p) && p > 0)
                    port = p;

                // Fallback: parse port from NAME attribute (ftp://user:***@host:port)
                if (portEl == null)
                {
                    var nameAttr = site.Attribute("NAME")?.Value;
                    if (nameAttr != null)
                    {
                        try
                        {
                            var uri = new Uri(nameAttr.Replace(":***", ""));
                            if (uri.Port > 0 && uri.Port != 21) port = uri.Port;
                            if (string.IsNullOrEmpty(host)) host = uri.Host;
                            if (string.IsNullOrEmpty(user)) user = uri.UserInfo;
                        }
                        catch { }
                    }
                }

                var name = site.Attribute("NAME")?.Value;
                // Clean up display name — strip ftp:// prefix if present
                if (name != null && name.Contains("://"))
                    name = host;
                name ??= host;

                var initDir = site.Element("INITDIR")?.Value?.Trim()
                           ?? site.Element("REMOTEPATH")?.Value?.Trim();

                var server = new ServerConfig
                {
                    Name = name,
                    Connection =
                    {
                        Host = host,
                        Port = port,
                        Username = user ?? "",
                        RootPath = !string.IsNullOrEmpty(initDir) ? initDir : "/"
                    },
                    Mount = { AutoMountOnStart = false }
                };

                // Check for SSL/TLS settings
                var ssl = site.Element("SSL")?.Value;
                var sslMode = site.Element("SSLMODE")?.Value;
                if (ssl == "True" || ssl == "1" || sslMode is "1" or "2" or "3")
                    server.Tls.PreferTls12 = true;

                // Proxy settings (FTPRush uses PROXY_HOST, PROXY_PORT, PROXY_USER, PROXY_TYPE)
                var proxyHost = site.Element("PROXY_HOST")?.Value?.Trim()
                             ?? site.Element("ProxyHost")?.Value?.Trim();
                if (!string.IsNullOrEmpty(proxyHost))
                {
                    var proxyPort = 1080;
                    var proxyPortStr = site.Element("PROXY_PORT")?.Value ?? site.Element("ProxyPort")?.Value;
                    if (int.TryParse(proxyPortStr, out var pp) && pp > 0)
                        proxyPort = pp;

                    var proxyUser = site.Element("PROXY_USER")?.Value?.Trim()
                                 ?? site.Element("ProxyUser")?.Value?.Trim() ?? "";

                    server.Connection.Proxy = new ProxyConfig
                    {
                        Enabled = true,
                        Host = proxyHost,
                        Port = proxyPort,
                        Username = proxyUser
                    };
                    Log.Debug("  with proxy: {ProxyHost}:{ProxyPort}", proxyHost, proxyPort);
                }

                // Skiplist / file filter patterns
                var skiplistEl = site.Element("SKIPLIST") ?? site.Element("MASK");
                if (skiplistEl != null)
                {
                    foreach (var rule in ParseSkiplistElement(skiplistEl))
                        server.SpreadSite.Skiplist.Add(rule);
                }

                results.Add(server);
                Log.Debug("FTPRush import: {Name} ({Host}:{Port})", name, host, port);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Skipping FTPRush site entry");
            }
        }

        Log.Information("FTPRush import: {Count} sites from {File}", results.Count, Path.GetFileName(xmlPath));
        return results;
    }

    /// <summary>
    /// Import sites from FlashFXP's .ftp export files (XML format).
    /// </summary>
    public static List<ServerConfig> ImportFlashFxpXml(string xmlPath)
    {
        var results = new List<ServerConfig>();
        var doc = XDocument.Load(xmlPath);

        foreach (var site in doc.Descendants("SITE"))
        {
            try
            {
                var host = site.Element("ADDRESS")?.Value?.Trim();
                if (string.IsNullOrEmpty(host)) continue;

                var port = 21;
                if (int.TryParse(site.Element("PORT")?.Value, out var p) && p > 0)
                    port = p;

                var user = site.Element("USERNAME")?.Value?.Trim() ?? "";
                var name = site.Attribute("NAME")?.Value ?? host;
                var password = site.Element("PASSWORD")?.Value?.Trim();

                var server = new ServerConfig
                {
                    Name = name,
                    Connection =
                    {
                        Host = host,
                        Port = port,
                        Username = user,
                        RootPath = "/"
                    },
                    Mount = { AutoMountOnStart = false }
                };

                // TLS detection
                var ssl = site.Element("SSL")?.Value?.Trim();
                if (!string.IsNullOrEmpty(ssl) && !ssl.Equals("NONE", StringComparison.OrdinalIgnoreCase))
                    server.Tls.PreferTls12 = true;

                // Proxy (FlashFXP XML may include PROXY_HOST etc.)
                var proxyHost = site.Element("PROXY_HOST")?.Value?.Trim()
                             ?? site.Element("PROXY")?.Element("HOST")?.Value?.Trim();
                if (!string.IsNullOrEmpty(proxyHost))
                {
                    var proxyPort = 1080;
                    var proxyPortStr = site.Element("PROXY_PORT")?.Value
                                    ?? site.Element("PROXY")?.Element("PORT")?.Value;
                    if (int.TryParse(proxyPortStr, out var pp) && pp > 0)
                        proxyPort = pp;

                    server.Connection.Proxy = new ProxyConfig
                    {
                        Enabled = true,
                        Host = proxyHost,
                        Port = proxyPort
                    };
                }

                // Save password to Credential Manager if present
                if (!string.IsNullOrEmpty(password) && !string.IsNullOrEmpty(user))
                    CredentialStore.SavePassword(host, port, user, password);

                results.Add(server);
                Log.Debug("FlashFXP XML import: {Name} ({Host}:{Port})", name, host, port);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Skipping FlashFXP XML site entry");
            }
        }

        Log.Information("FlashFXP XML import: {Count} sites from {File}", results.Count, Path.GetFileName(xmlPath));
        return results;
    }

    /// <summary>
    /// Import sites from FlashFXP's Sites.dat (INI format).
    /// </summary>
    public static List<ServerConfig> ImportFlashFxpDat(string datPath)
    {
        var results = new List<ServerConfig>();
        var lines = File.ReadAllLines(datPath);

        string? currentSection = null;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i <= lines.Length; i++)
        {
            var line = i < lines.Length ? lines[i].Trim() : null;

            // New section or end of file — flush previous section
            if (line == null || (line.StartsWith('[') && line.EndsWith(']')))
            {
                if (currentSection != null && values.Count > 0)
                {
                    var server = ParseFlashFxpSection(currentSection, values);
                    if (server != null)
                        results.Add(server);
                }

                if (line != null)
                {
                    currentSection = line[1..^1]; // strip [ ]
                    values.Clear();
                }
                continue;
            }

            // Key=Value
            var eq = line.IndexOf('=');
            if (eq > 0)
                values[line[..eq]] = line[(eq + 1)..];
        }

        Log.Information("FlashFXP import: {Count} sites from {File}", results.Count, Path.GetFileName(datPath));
        return results;
    }

    private static ServerConfig? ParseFlashFxpSection(string sectionName, Dictionary<string, string> values)
    {
        try
        {
            var host = values.GetValueOrDefault("IP", "").Trim();
            if (string.IsNullOrEmpty(host)) return null;

            var port = 21;
            if (values.TryGetValue("port", out var portStr) && int.TryParse(portStr, out var p) && p > 0)
                port = p;

            var user = values.GetValueOrDefault("user", "").Trim();

            // Display name: strip folder prefix (FlashFXP uses Group\SiteName)
            var name = sectionName;
            var lastSlash = name.LastIndexOf('\\');
            if (lastSlash >= 0) name = name[(lastSlash + 1)..];
            if (string.IsNullOrEmpty(name)) name = host;

            var remotePath = values.GetValueOrDefault("remotepath", "").Trim();
            if (string.IsNullOrEmpty(remotePath))
                remotePath = values.GetValueOrDefault("path", "").Trim();

            var server = new ServerConfig
            {
                Name = name,
                Connection =
                {
                    Host = host,
                    Port = port,
                    Username = user,
                    RootPath = !string.IsNullOrEmpty(remotePath) ? remotePath : "/"
                },
                Mount = { AutoMountOnStart = false }
            };

            // Check for TLS — FlashFXP uses an Options bitmask or DataEncryption field
            var dataEnc = values.GetValueOrDefault("DataEncryption", "");
            if (dataEnc is "1" or "2" or "3") // Explicit, Implicit, or Auth TLS
                server.Tls.PreferTls12 = true;

            Log.Debug("FlashFXP import: {Name} ({Host}:{Port})", name, host, port);
            return server;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Skipping FlashFXP section: {Section}", sectionName);
            return null;
        }
    }

    /// <summary>
    /// Import skiplist rules from a text file. Supports multiple formats:
    /// - One glob pattern per line (e.g. *.nfo, *.jpg, Sample)
    /// - FTPRush-style prefixes: %D% = directories only, %F% = files only
    /// - Lines starting with # or ; are comments
    /// - Lines starting with + are Allow rules, all others are Deny
    /// </summary>
    public static List<SkiplistRule> ImportSkiplist(string filePath)
    {
        var rules = new List<SkiplistRule>();

        foreach (var rawLine in File.ReadLines(filePath))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line[0] is '#' or ';')
                continue;

            var action = SkiplistAction.Deny;

            // + prefix = Allow rule
            if (line.StartsWith('+'))
            {
                action = SkiplistAction.Allow;
                line = line[1..].Trim();
            }

            var rule = ParseSkipPattern(line);
            if (rule == null) continue;
            rule.Action = action;

            // Detect regex (contains regex-only metacharacters beyond simple glob * and ?)
            rule.IsRegex = rule.Pattern.AsSpan().IndexOfAny("([{^$|") >= 0;

            rules.Add(rule);
        }

        Log.Information("Skiplist import: {Count} rules from {File}", rules.Count, Path.GetFileName(filePath));
        return rules;
    }

    /// <summary>
    /// Parse skiplist patterns from an FTPRush XML element (SKIPLIST or MASK child elements).
    /// </summary>
    private static List<SkiplistRule> ParseSkiplistElement(XElement el)
    {
        var rules = new List<SkiplistRule>();

        // Try child elements first, then split text value by newlines/semicolons
        var hasChildren = false;
        foreach (var child in el.Elements())
        {
            hasChildren = true;
            var rule = ParseSkipPattern(child.Value.Trim());
            if (rule != null) rules.Add(rule);
        }

        if (!hasChildren && !string.IsNullOrWhiteSpace(el.Value))
        {
            foreach (var part in el.Value.Split(['\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries))
            {
                var rule = ParseSkipPattern(part.Trim());
                if (rule != null) rules.Add(rule);
            }
        }

        return rules;
    }

    /// <summary>
    /// Parse a single skip pattern with optional %D%/%F% prefix.
    /// </summary>
    private static SkiplistRule? ParseSkipPattern(string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return null;

        var matchDirs = true;
        var matchFiles = true;

        if (pattern.StartsWith("%D%", StringComparison.OrdinalIgnoreCase))
        {
            matchFiles = false;
            pattern = pattern[3..].Trim();
        }
        else if (pattern.StartsWith("%F%", StringComparison.OrdinalIgnoreCase))
        {
            matchDirs = false;
            pattern = pattern[3..].Trim();
        }

        if (string.IsNullOrEmpty(pattern)) return null;

        return new SkiplistRule
        {
            Pattern = pattern,
            Action = SkiplistAction.Deny,
            MatchDirectories = matchDirs,
            MatchFiles = matchFiles
        };
    }
}
