using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace GlDrive.Mcp;

internal sealed class ControlApiClient : IDisposable
{
    internal const int MaxResponseBytes = 16 * 1024 * 1024;
    internal const int MaxRequestBytes = 8 * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly Func<ControlConnection> _connection;

    public ControlApiClient() : this(CreateHandler(), ControlConnection.Load) { }
    internal ControlApiClient(HttpMessageHandler handler, Func<ControlConnection> connection)
    {
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _connection = connection;
    }

    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        MaxResponseHeadersLength = 32
    };

    public Task<JsonElement> GetCatalogAsync(CancellationToken cancellationToken) =>
        SendAsync("automation/tools", null, TimeSpan.FromSeconds(5), cancellationToken);

    public Task<JsonElement> CallAsync(string name, IDictionary<string, JsonElement>? arguments, CancellationToken cancellationToken) =>
        SendAsync("automation/call", JsonSerializer.SerializeToElement(new { name, arguments = arguments ?? new Dictionary<string, JsonElement>() }),
            TimeSpan.FromSeconds(120), cancellationToken);

    private async Task<JsonElement> SendAsync(string route, JsonElement? body, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // Reload for every request so normal app restarts and token rotations need no MCP reconfiguration.
        var connection = _connection();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using var request = new HttpRequestMessage(body.HasValue ? HttpMethod.Post : HttpMethod.Get, new Uri(connection.BaseUri, route));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body.HasValue)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(body.Value);
            if (bytes.Length > MaxRequestBytes) throw new BridgeException("Tool arguments exceed the 8 MiB request limit. Use smaller chunks.");
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if ((int)response.StatusCode is >= 300 and < 400)
                throw new BridgeException("The local control API returned a redirect; redirects are never followed.");
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new BridgeException("GlDrive rejected the local control API token. Check the current app settings or remove stale GLDRIVE_CONTROL_TOKEN overrides.");
            if (response.StatusCode == HttpStatusCode.NotFound && body is null)
                throw new BridgeException("This GlDrive version has no MCP automation catalog. Install a version with MCP support and restart the app.");
            if (response.Content.Headers.ContentLength is > MaxResponseBytes)
                throw new BridgeException("GlDrive response exceeds 16 MiB. Narrow the query or request a smaller page.");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, deadline.Token)) > 0)
            {
                if (buffer.Length + read > MaxResponseBytes)
                    throw new BridgeException("GlDrive response exceeds 16 MiB. Narrow the query or request a smaller page.");
                buffer.Write(chunk, 0, read);
            }
            if (!response.IsSuccessStatusCode)
            {
                // Error bodies are bounded and the transport credential is never echoed to the model.
                string detail = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length)
                    .Replace(connection.Token, "[redacted]", StringComparison.Ordinal);
                if (detail.Length > 4096) detail = detail[..4096];
                throw new BridgeException($"GlDrive returned HTTP {(int)response.StatusCode}: {detail}");
            }
            using var json = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
            return json.RootElement.Clone();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new BridgeException("GlDrive did not respond before the timeout. A write may already have completed; read the current state before retrying."); }
        catch (HttpRequestException)
        { throw new BridgeException("Cannot reach GlDrive on its configured loopback port. Start GlDrive, enable its local control API, and retry. If this was a write, inspect current state before retrying."); }
        catch (IOException)
        { throw new BridgeException("The GlDrive connection closed before the response completed. If this was a write, inspect current state before retrying."); }
        catch (JsonException)
        { throw new BridgeException("GlDrive returned an invalid JSON response. Check that the app and MCP bridge are compatible versions."); }
    }

    public void Dispose() => _http.Dispose();
}
