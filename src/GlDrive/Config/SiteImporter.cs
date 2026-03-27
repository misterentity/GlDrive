using System.IO;
using System.Xml.Linq;
using Serilog;

namespace GlDrive.Config;

/// <summary>
/// Imports FTP site definitions from FTPRush (RushSite.xml) and FlashFXP (Sites.dat).
/// Passwords are not imported — they use proprietary encryption and the user should re-enter them.
/// </summary>
public static class SiteImporter
{
    /// <summary>
    /// Import sites from FTPRush's RushSite.xml.
    /// </summary>
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
    /// Import sites from FlashFXP's Sites.dat (INI format).
    /// </summary>
    public static List<ServerConfig> ImportFlashFxp(string datPath)
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
}
