using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace GlDrive.Services.Control;

/// <summary>
/// One control-API request. Endpoints receive this and never see HttpListenerContext, so
/// the response shape and error envelope stay in one place.
/// </summary>
public sealed class ControlRequest
{
    /// <summary>
    /// The one definition of the control API's JSON shape — camelCase, nulls omitted,
    /// indented. Shared with ControlApi.cs (which needs it for the loopback/token gate
    /// responses and the top-level catch-all, both ahead of routing and so outside any
    /// ControlRequest) rather than duplicated, so the wire format can't drift between them.
    /// </summary>
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private readonly HttpListenerContext? _ctx;
    private readonly IReadOnlyDictionary<string, string> _parameters;
    private readonly NameValueCollection _query;

    public string Path { get; }

    private ControlRequest(HttpListenerContext? ctx, string path,
        IReadOnlyDictionary<string, string> parameters, NameValueCollection query)
    {
        _ctx = ctx;
        Path = path;
        _parameters = parameters;
        _query = query;
    }

    public static ControlRequest FromContext(HttpListenerContext ctx, string path,
        IReadOnlyDictionary<string, string> parameters)
        => new(ctx, path, parameters, ctx.Request.QueryString);

    /// <summary>Parsing-only instance for unit tests; responding on it throws.</summary>
    public static ControlRequest ForTesting(
        IReadOnlyDictionary<string, string> parameters, NameValueCollection query)
        => new(null, "/test", parameters, query);

    public string? Param(string name) => _parameters.TryGetValue(name, out var v) ? v : null;

    public string? Query(string name) => _query[name];

    public int QueryInt(string name, int fallback, int min, int max)
        => int.TryParse(_query[name], out var n) ? Math.Clamp(n, min, max) : fallback;

    /// <summary>
    /// Largest request body any endpoint here has a use for. The biggest real payload is a
    /// race start, which is two short strings — 64 KB is three orders of magnitude of slack.
    /// </summary>
    internal const int MaxBodyBytes = 64 * 1024;

    /// <summary>
    /// Reads the request body, refusing anything over <see cref="MaxBodyBytes"/>.
    ///
    /// This used to be an unbounded <c>ReadToEndAsync</c>: a 20 MB body took the app from
    /// 238 MB to 585 MB resident, because the bytes are buffered and then materialised again
    /// as a UTF-16 string. Loopback-only binding limits who can do that, but a buggy script
    /// is as good as a hostile one, and nothing here ever needs a large body.
    ///
    /// Returns null when the body is too large — the caller answers 413 rather than trying
    /// to parse a truncated document, which would produce a misleading "invalid JSON".
    /// </summary>
    public async Task<string?> ReadBodyAsync()
    {
        if (_ctx == null) return "";

        // Trust the declared length when it is present and already over the cap: refuse
        // without reading. When absent or lying, the read below is still bounded.
        if (_ctx.Request.ContentLength64 > MaxBodyBytes) return null;

        var buffer = new byte[MaxBodyBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await _ctx.Request.InputStream.ReadAsync(buffer.AsMemory(total, buffer.Length - total));
            if (read == 0) break;
            total += read;
        }

        if (total > MaxBodyBytes) return null;
        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    public async Task RespondAsync(int status, object payload)
    {
        if (_ctx == null) throw new InvalidOperationException("No context — test instance");

        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        _ctx.Response.StatusCode = status;
        _ctx.Response.ContentType = "application/json";
        _ctx.Response.ContentLength64 = bytes.Length;
        await _ctx.Response.OutputStream.WriteAsync(bytes);
        _ctx.Response.Close();
    }

    public Task ErrorAsync(int status, string code, string error, string? detail = null)
        => RespondAsync(status, new { error, code, detail, path = Path });
}
