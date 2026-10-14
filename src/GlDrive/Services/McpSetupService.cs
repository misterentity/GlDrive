using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using GlDrive.Config;

namespace GlDrive.Services;

/// <summary>Copyable client setup. These values do not register a client or contain credentials.</summary>
public sealed record McpSetupInfo(string AdapterPath, bool AdapterAvailable, string Status,
    string ClaudeCodeCommand, string CodexCommand, string ClaudeDesktopJson);

/// <summary>A read-only check of the saved local API connection, not client registration status.</summary>
public sealed record McpProbeResult(bool Connected, string Message, int? AppToolCount = null, int? McpToolCount = null);

public static class McpSetupService
{
    private const string ConnectionStatusTool = "fmountr_connection_status";
    internal const int MaxCatalogBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    public static McpSetupInfo GetSetup(string? baseDirectory = null)
    {
        string adapter = Path.GetFullPath(Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "mcp", "GlDrive.Mcp.exe"));
        bool available = File.Exists(adapter);
        string quotedPath = "'" + adapter.Replace("'", "''", StringComparison.Ordinal) + "'";
        return new McpSetupInfo(adapter, available,
            available
                ? "MCP adapter found. Add it to your client using one of the commands below."
                : "MCP adapter is missing from this installation. Install or repair the full GlDrive release, then reopen this panel. Commands below are setup instructions, not completed registrations.",
            "claude mcp add --transport stdio --scope user fmountr -- " + quotedPath,
            "codex mcp add fmountr -- " + quotedPath,
            JsonSerializer.Serialize(new
            {
                mcpServers = new
                {
                    fmountr = new { command = adapter, args = Array.Empty<string>() }
                }
            }, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static Task<McpProbeResult> ProbeAsync(ControlApiConfig config, CancellationToken ct = default)
        => ProbeAsync(config, CreateHandler(), ProbeTimeout, ct);

    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = ProbeTimeout,
        MaxResponseHeadersLength = 16
    };

    internal static async Task<McpProbeResult> ProbeAsync(ControlApiConfig config, HttpMessageHandler handler,
        TimeSpan timeout, CancellationToken ct = default)
    {
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        ct.ThrowIfCancellationRequested();
        // Snapshot the saved runtime values before awaiting; changing a draft does not
        // change the address or credential of a check already in progress.
        bool enabled = config.Enabled;
        int port = config.Port;
        string token = config.Token;
        if (!enabled)
            return Unavailable("MCP / local control API is disabled. Enable it and apply the settings before checking the connection.");
        if (port is < 1 or > 65535)
            return Unavailable("The saved local API port is invalid. Choose a valid port and apply the settings.");
        if (string.IsNullOrWhiteSpace(token))
            return Unavailable("The local API has no authentication token. Apply enabled MCP settings so GlDrive can generate one.");
        if (token.Length > 4096 || token.Any(c => c <= ' ' || c >= 127))
            return Unavailable("The saved local API token is invalid. Repair the MCP settings before checking again.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/automation/tools");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return Unavailable("The local API rejected authentication. Apply the current MCP settings and check again.");
            if ((int)response.StatusCode is >= 300 and < 400)
                return Unavailable("The configured local API returned a redirect. Redirects are not followed; check the port and apply the settings.");
            if (response.StatusCode == HttpStatusCode.NotFound)
                return Unavailable("The listening app has no MCP catalog. Restart GlDrive or install a release with MCP support.");
            if (!response.IsSuccessStatusCode)
                return Unavailable($"The local API returned HTTP {(int)response.StatusCode}. Check GlDrive diagnostics and retry.");
            if (response.Content.Headers.ContentLength is > MaxCatalogBytes)
                return Unavailable("The local API catalog exceeds the 2 MiB check limit. Check the app version and diagnostics.");

            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, deadline.Token).ConfigureAwait(false)) != 0)
            {
                if (buffer.Length + read > MaxCatalogBytes)
                    return Unavailable("The local API catalog exceeds the 2 MiB check limit. Check the app version and diagnostics.");
                buffer.Write(chunk, 0, read);
            }
            using var document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("tools", out var tools)
                || tools.ValueKind != JsonValueKind.Array)
                return InvalidCatalog();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tool in tools.EnumerateArray())
            {
                if (tool.ValueKind != JsonValueKind.Object
                    || !tool.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(name.GetString()) || name.GetString() == ConnectionStatusTool
                    || !names.Add(name.GetString()!)
                    || !tool.TryGetProperty("inputSchema", out var schema) || schema.ValueKind != JsonValueKind.Object)
                    return InvalidCatalog();
            }
            int count = names.Count;
            return new McpProbeResult(true,
                $"Local API connected: {count} app tools available ({count + 1} including the MCP connection-status tool). This checks the app connection; client registration is separate.",
                count, count + 1);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Preserve cancellation without forwarding exception text from a handler.
            throw new OperationCanceledException(ct);
        }
        catch (OperationCanceledException)
        { return Unavailable("The local API did not respond in time. Check that GlDrive's listener is running, then retry."); }
        catch (HttpRequestException)
        { return Unavailable("Cannot reach the local API. Apply MCP settings, check the saved port, and keep GlDrive running."); }
        catch (IOException)
        { return Unavailable("The local API connection closed before the check completed. Retry or check GlDrive diagnostics."); }
        catch (JsonException)
        { return InvalidCatalog(); }
        catch (Exception)
        { return Unavailable("The local API check could not complete. Check GlDrive diagnostics and retry."); }
    }

    private static McpProbeResult Unavailable(string message) => new(false, message);
    private static McpProbeResult InvalidCatalog() => Unavailable("The local API returned an invalid MCP catalog. Restart GlDrive or repair the installation.");
}
