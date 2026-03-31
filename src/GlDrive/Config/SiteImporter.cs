using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

        if (ext.Equals(".json", StringComparison.OrdinalIgnoreCase))
            return ImportFtpRushJson(filePath);

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
                // FlashFXP uses <ADDRESS>, FTPRush uses <HOST> or <Host>
                if (firstSite?.Element("ADDRESS") != null)
                    return ImportFlashFxpXml(filePath);
            }
            catch { }

            // Default to FTPRush for XML (handles both old ALLCAPS and new camelCase)
            return ImportFtpRush(filePath);
        }

        try { return ImportFtpRushJson(filePath); }
        catch { try { return ImportFtpRush(filePath); } catch { return ImportFlashFxpDat(filePath); } }
    }

    /// <summary>
    /// Import sites from FTPRush's JSON export (site.json / sites.json).
    /// Tree structure: RootItem.Children[].Server + recursive Children.
    /// </summary>
    public static List<ServerConfig> ImportFtpRushJson(string jsonPath)
    {
        var results = new List<ServerConfig>();
        var json = File.ReadAllText(jsonPath);
        using var doc = JsonDocument.Parse(json);

        var root = doc.RootElement;
        if (root.TryGetProperty("RootItem", out var rootItem))
            WalkFtpRushNode(rootItem, results);
        else if (root.TryGetProperty("Children", out _))
            WalkFtpRushNode(root, results);

        Log.Information("FTPRush JSON import: {Count} sites from {File}", results.Count, Path.GetFileName(jsonPath));
        return results;
    }

    private static void WalkFtpRushNode(JsonElement node, List<ServerConfig> results)
    {
        // If this node has a Server, import it
        if (node.TryGetProperty("Server", out var serverEl) && serverEl.ValueKind == JsonValueKind.Object)
        {
            var server = ParseFtpRushJsonServer(node, serverEl);
            if (server != null)
                results.Add(server);
        }

        // Recurse into children
        if (node.TryGetProperty("Children", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
                WalkFtpRushNode(child, results);
        }
    }

    private static ServerConfig? ParseFtpRushJsonServer(JsonElement node, JsonElement s)
    {
        try
        {
            var host = s.GetProperty("Host").GetString()?.Trim();
            if (string.IsNullOrEmpty(host) || host == "127.0.0.1") return null;

            var port = s.TryGetProperty("Port", out var portEl) ? portEl.GetInt32() : 21;
            if (port <= 0) port = 21;

            var user = s.TryGetProperty("Username", out var userEl) ? userEl.GetString()?.Trim() ?? "" : "";
            var name = node.TryGetProperty("Name", out var nameEl) ? nameEl.GetString() ?? host : host;
            if (string.IsNullOrEmpty(name) || name == "New Site") name = host;

            var remotePath = s.TryGetProperty("DefaultRemotePath", out var rpEl) ? rpEl.GetString()?.Trim() ?? "" : "";

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

            // TLS: FTPEnryptMode 1=Implicit, 2=Explicit
            if (s.TryGetProperty("FTPEnryptMode", out var encEl))
            {
                var mode = encEl.GetInt32();
                if (mode is 1 or 2)
                    server.Tls.PreferTls12 = true;
            }

            // Password: Base64-encoded
            if (s.TryGetProperty("Base64Password", out var pwEl))
            {
                var b64 = pwEl.GetString();
                if (!string.IsNullOrEmpty(b64))
                {
                    try
                    {
                        var password = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                        if (!string.IsNullOrEmpty(password) && !string.IsNullOrEmpty(user))
                            CredentialStore.SavePassword(host, port, user, password);
                    }
                    catch { }
                }
            }

            // File filters → skiplist
            if (s.TryGetProperty("FileFilters", out var filters) && filters.ValueKind == JsonValueKind.Array)
            {
                foreach (var filter in filters.EnumerateArray())
                {
                    var pattern = filter.TryGetProperty("Pattern", out var patEl) ? patEl.GetString() : null;
                    if (pattern == null && filter.ValueKind == JsonValueKind.String)
                        pattern = filter.GetString();
                    if (string.IsNullOrEmpty(pattern)) continue;

                    var rule = ParseSkipPattern(pattern);
                    if (rule != null)
                        server.SpreadSite.Skiplist.Add(rule);
                }
            }

            Log.Debug("FTPRush JSON import: {Name} ({Host}:{Port})", name, host, port);
            return server;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Skipping FTPRush JSON site entry");
            return null;
        }
    }

    public static List<ServerConfig> ImportFtpRush(string xmlPath)
    {
        var results = new List<ServerConfig>();

        // Load XML with repair for malformed exports (unclosed SITE tags, etc.)
        XDocument doc;
        try
        {
            doc = XDocument.Load(xmlPath);
        }
        catch (System.Xml.XmlException)
        {
            Log.Warning("FTPRush XML is malformed, attempting repair: {File}", Path.GetFileName(xmlPath));
            var repaired = RepairFtpRushXml(File.ReadAllText(xmlPath));
            doc = XDocument.Parse(repaired);
        }

        // FTPRush XML can nest SITE elements inside GROUP elements or under FTPRUSHSITES
        foreach (var site in doc.Descendants("SITE"))
        {
            try
            {
                // FTPRush 2+ uses camelCase (Host, Username, Port) matching Server class
                // Older versions use ALLCAPS (HOST, USER, PORT)
                var host = (site.Element("Host") ?? site.Element("HOST"))?.Value?.Trim();
                var user = (site.Element("Username") ?? site.Element("USER"))?.Value?.Trim();
                if (string.IsNullOrEmpty(host)) continue;

                // Convert packed integer IPs (e.g. 1735868216 → dotted notation)
                host = ResolvePackedIp(host);

                var port = 21;
                var portStr = (site.Element("Port") ?? site.Element("PORT"))?.Value?.Trim();
                if (int.TryParse(portStr, out var p) && p > 0)
                    port = p;

                // Fallback: parse from NAME attribute (ftp://user:***@host:port)
                if (portStr == null)
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

                var name = site.Attribute("NAME")?.Value ?? site.Element("Name")?.Value;
                if (name != null && name.Contains("://"))
                    name = host;
                if (string.IsNullOrEmpty(name) || name == "New Site")
                    name = host;

                var initDir = (site.Element("DefaultRemotePath") ?? site.Element("INITDIR")
                            ?? site.Element("REMOTEPATH"))?.Value?.Trim();

                // DISABLE element — skip disabled sites or import as disabled
                var disabled = (site.Element("DISABLE") ?? site.Element("Disable"))?.Value?.Trim();

                var server = new ServerConfig
                {
                    Name = name,
                    Enabled = !string.Equals(disabled, "True", StringComparison.OrdinalIgnoreCase),
                    Connection =
                    {
                        Host = host,
                        Port = port,
                        Username = user ?? "",
                        RootPath = !string.IsNullOrEmpty(initDir) ? initDir : "/"
                    },
                    Mount = { AutoMountOnStart = false }
                };

                // TLS: FTPRush 2+ uses FTPEnryptMode (1=Implicit, 2=Explicit)
                // Older uses <SSL ONDATA="True" LIST="True">mode</SSL> where mode: 1=Auth TLS, 3=Implicit
                var encMode = site.Element("FTPEnryptMode")?.Value?.Trim();
                var sslEl = site.Element("SSL");
                var sslValue = sslEl?.Value?.Trim();
                var sslMode = site.Element("SSLMODE")?.Value?.Trim();
                if (encMode is "1" or "2" ||
                    sslValue is "1" or "2" or "3" || sslValue == "True" ||
                    sslMode is "1" or "2" or "3")
                {
                    server.Tls.PreferTls12 = true;
                }

                // Password: FTPRush 2+ stores Base64Password, older stores PASS (proprietary encryption)
                var b64pw = site.Element("Base64Password")?.Value;
                if (!string.IsNullOrEmpty(b64pw))
                {
                    try
                    {
                        var password = Encoding.UTF8.GetString(Convert.FromBase64String(b64pw));
                        if (!string.IsNullOrEmpty(password) && !string.IsNullOrEmpty(user))
                            CredentialStore.SavePassword(host, port, user, password);
                    }
                    catch { }
                }
                else
                {
                    // Old PASS element uses proprietary encryption — try XOR decode
                    var passHex = (site.Element("PASS") ?? site.Element("Pass"))?.Value?.Trim();
                    if (!string.IsNullOrEmpty(passHex))
                    {
                        var decoded = DecodeFtpRushPassword(passHex);
                        if (!string.IsNullOrEmpty(decoded) && !string.IsNullOrEmpty(user))
                            CredentialStore.SavePassword(host, port, user, decoded);
                        else
                            Log.Debug("FTPRush PASS could not be decoded for {Name} — re-enter password manually", name);
                    }
                }

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

                // Skiplist: SKIP (old format with 8-digit flags), SKIPLIST, MASK, FileFilters
                var skipEl = site.Element("SKIP") ?? site.Element("Skip");
                if (skipEl != null)
                {
                    foreach (var rule in ParseFtpRushSkipElement(skipEl))
                        server.SpreadSite.Skiplist.Add(rule);
                }
                else
                {
                    var skiplistEl = site.Element("SKIPLIST") ?? site.Element("MASK");
                    if (skiplistEl != null)
                    {
                        foreach (var rule in ParseSkiplistElement(skiplistEl))
                            server.SpreadSite.Skiplist.Add(rule);
                    }
                }

                // Pool size from LOGIN MAX attribute
                var loginEl = site.Element("LOGIN") ?? site.Element("Login");
                var maxStr = loginEl?.Attribute("MAX")?.Value ?? loginEl?.Attribute("Max")?.Value;
                if (int.TryParse(maxStr, out var maxConn) && maxConn > 0)
                    server.Pool.PoolSize = Math.Min(maxConn, 10);

                results.Add(server);
                Log.Debug("FTPRush import: {Name} ({Host}:{Port}, TLS={Tls}, skip={Skip})",
                    name, host, port, server.Tls.PreferTls12, server.SpreadSite.Skiplist.Count);
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
    /// Parse FTPRush old-format SKIP element with 8-digit flag prefix per item.
    /// Format: &lt;SKIP&gt;&lt;I&gt;10211111Proof&lt;/I&gt;...&lt;/SKIP&gt;
    /// Flag prefix: [nameOrPath][0][action][target]1111
    ///   pos 0: 1=match name only, 0=match full path
    ///   pos 2: 2=deny/skip
    ///   pos 3: 1=directories, 3=files
    /// </summary>
    private static List<SkiplistRule> ParseFtpRushSkipElement(XElement skipEl)
    {
        var rules = new List<SkiplistRule>();

        foreach (var item in skipEl.Elements("I"))
        {
            var raw = item.Value.Trim();
            if (raw.Length <= 8) continue;

            // First 8 chars are flags, rest is the pattern
            var flags = raw[..8];
            var pattern = raw[8..];
            if (string.IsNullOrEmpty(pattern)) continue;

            var matchDirs = true;
            var matchFiles = true;

            // Position 3: 1=directories only, 3=files only
            if (flags.Length > 3)
            {
                if (flags[3] == '1') matchFiles = false;  // dirs only
                else if (flags[3] == '3') matchDirs = false;  // files only
            }

            rules.Add(new SkiplistRule
            {
                Pattern = pattern,
                Action = SkiplistAction.Deny,
                MatchDirectories = matchDirs,
                MatchFiles = matchFiles
            });
        }

        // Fallback: try as plain text if no <I> children found
        if (rules.Count == 0 && !string.IsNullOrWhiteSpace(skipEl.Value))
        {
            foreach (var part in skipEl.Value.Split(['\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries))
            {
                var rule = ParseSkipPattern(part.Trim());
                if (rule != null) rules.Add(rule);
            }
        }

        return rules;
    }

    /// <summary>
    /// Convert a packed 32-bit integer IP to dotted notation (e.g. 1735868216 → "103.110.92.72").
    /// Returns the original string if it's already a hostname or standard IP.
    /// </summary>
    private static string ResolvePackedIp(string host)
    {
        if (!uint.TryParse(host, out var packed)) return host;
        // Ignore small numbers that aren't valid IPs
        if (packed < 16777216) return host; // < 1.0.0.0

        try
        {
            // FTPRush stores IPs as 32-bit integers in network byte order
            var bytes = BitConverter.GetBytes(packed);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            var ip = new IPAddress(bytes).ToString();
            Log.Debug("Resolved packed IP {Packed} → {Ip}", host, ip);
            return ip;
        }
        catch
        {
            return host;
        }
    }

    /// <summary>
    /// Decode FTPRush old-format password (hex-encoded XOR with fixed key "RushSite").
    /// Returns null if decoding fails or produces non-printable characters.
    /// </summary>
    private static string? DecodeFtpRushPassword(string hexStr)
    {
        // Validate hex string
        if (hexStr.Length % 2 != 0) return null;
        if (!Regex.IsMatch(hexStr, @"^[0-9A-Fa-f]+$")) return null;

        try
        {
            var key = "RushSite"u8;
            var bytes = Convert.FromHexString(hexStr);
            for (var i = 0; i < bytes.Length; i++)
                bytes[i] ^= key[i % key.Length];

            var result = Encoding.ASCII.GetString(bytes);
            // Verify it's printable
            if (result.Any(c => c < 0x20 || c > 0x7E))
                return null;
            return result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Repair common FTPRush XML issues: unclosed SITE tags, missing root element.
    /// </summary>
    private static string RepairFtpRushXml(string xml)
    {
        var sb = new StringBuilder(xml.Length + 256);
        var siteOpen = false;

        foreach (var rawLine in xml.Split('\n'))
        {
            var trimmed = rawLine.TrimStart();

            // If a new SITE or GROUP starts while a SITE is unclosed, close it first
            if (siteOpen &&
                (trimmed.StartsWith("<SITE ", StringComparison.OrdinalIgnoreCase) ||
                 trimmed.StartsWith("<GROUP ", StringComparison.OrdinalIgnoreCase) ||
                 trimmed.StartsWith("</GROUP>", StringComparison.OrdinalIgnoreCase)))
            {
                sb.AppendLine("    </SITE>");
                siteOpen = false;
            }

            if (trimmed.StartsWith("<SITE ", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.Contains("/>"))
            {
                siteOpen = true;
            }
            else if (trimmed.StartsWith("</SITE>", StringComparison.OrdinalIgnoreCase))
            {
                siteOpen = false;
            }

            sb.Append(rawLine);
            sb.Append('\n');
        }

        if (siteOpen)
            sb.AppendLine("    </SITE>");

        return sb.ToString();
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
