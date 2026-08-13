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
    private static readonly JsonSerializerOptions Json = new()
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

    public async Task<string> ReadBodyAsync()
    {
        if (_ctx == null) return "";
        using var reader = new StreamReader(_ctx.Request.InputStream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
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
