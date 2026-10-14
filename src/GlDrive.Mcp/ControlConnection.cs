using System.Globalization;
using System.Text.Json;

namespace GlDrive.Mcp;

internal sealed class BridgeException(string message) : Exception(message);

internal sealed class ControlConnection(int port, string token)
{
    public int Port { get; } = port;
    public string Token { get; } = token;
    public Uri BaseUri { get; } = new($"http://127.0.0.1:{port}/");

    // Never put credentials in record-generated ToString(), URLs, stdout, or diagnostics.
    public override string ToString() => $"GlDrive loopback control API on port {Port}";

    public static ControlConnection Load() => Load(Environment.GetEnvironmentVariable,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GlDrive", "appsettings.json"));

    internal static ControlConnection Load(Func<string, string?> environment, string defaultPath)
    {
        string? token = environment("GLDRIVE_CONTROL_TOKEN");
        string? portText = environment("GLDRIVE_CONTROL_PORT");
        int port = 8756;
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(portText))
        {
            string path = environment("GLDRIVE_CONFIG_PATH") ?? defaultPath;
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (file.Length > 8 * 1024 * 1024)
                    throw new BridgeException("GlDrive configuration exceeds the 8 MiB read limit.");
                // StreamReader handles the BOM used by some existing installations.
                using var reader = new StreamReader(file);
                using var json = JsonDocument.Parse(reader.ReadToEnd());
                var control = FindProperty(json.RootElement, "ControlApi");
                if (control.ValueKind != JsonValueKind.Object)
                    throw new BridgeException("Enable the local control API in GlDrive settings, then retry.");
                if (FindProperty(control, "Enabled").ValueKind != JsonValueKind.True)
                    throw new BridgeException("GlDrive's local control API is disabled. Enable it in GlDrive settings, then retry.");
                if (string.IsNullOrWhiteSpace(token)) token = FindProperty(control, "Token").GetString();
                var configuredPort = FindProperty(control, "Port");
                if (configuredPort.ValueKind != JsonValueKind.Undefined) port = configuredPort.GetInt32();
            }
            catch (BridgeException) { throw; }
            catch (FileNotFoundException)
            { throw new BridgeException("GlDrive settings were not found. Start GlDrive and enable its local control API."); }
            catch (DirectoryNotFoundException)
            { throw new BridgeException("GlDrive settings were not found. Start GlDrive and enable its local control API."); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException or ArgumentException)
            { throw new BridgeException("Cannot read GlDrive control API settings. Check GLDRIVE_CONFIG_PATH and the app's settings, then retry."); }
        }
        if (!string.IsNullOrWhiteSpace(portText) && !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port))
            throw new BridgeException("GLDRIVE_CONTROL_PORT must be an integer from 1 to 65535.");
        if (port is < 1 or > 65535)
            throw new BridgeException("The GlDrive control API port must be from 1 to 65535.");
        if (string.IsNullOrWhiteSpace(token))
            throw new BridgeException("GlDrive has no control API token. Enable the local control API in GlDrive settings.");
        if (token.Length > 4096 || token.Any(c => c <= ' ' || c >= 127))
            throw new BridgeException("The GlDrive control API token is invalid. Regenerate it in GlDrive settings.");
        return new ControlConnection(port, token);
    }

    private static JsonElement FindProperty(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return default;
    }
}
