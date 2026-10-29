using System.Text.Json;
using GlDrive.Services.Automation;
using Serilog;

namespace GlDrive.Services.Control.Endpoints;

public sealed class AutomationEndpoints(AutomationRegistry registry) : IControlEndpoint
{
    public void Register(RouteTable routes)
    {
        routes.Map("GET", "/automation/tools", r => r.RespondAsync(200, new { tools = registry.Tools.Select(t => t.ToProtocol()).ToArray() }));
        routes.Map("POST", "/automation/call", Call);
    }

    private async Task Call(ControlRequest request)
    {
        var body = await request.ReadBodyAsync();
        if (body == null) { await request.ErrorAsync(413, "body_too_large", "Request exceeds 64 KiB"); return; }
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
                throw new ArgumentException("name must be a string");
            root.TryGetProperty("arguments", out var args);
            var result = await registry.CallAsync(name.GetString()!, args, request.Cancellation, request.Principal);
            await request.RespondAsync(200, result ?? new { });
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException)
        { await request.ErrorAsync(400, "invalid_arguments", ex is JsonException ? "Malformed JSON" : ex.Message); }
        catch (KeyNotFoundException)
        { await request.ErrorAsync(404, "not_found", "Unknown tool, entity, or expired job"); }
        catch (AutomationDeniedException ex)
        { await request.ErrorAsync(403, ex.Code, "Client scope does not permit this operation; the connection remains authenticated"); }
        catch (OperationCanceledException) when (!request.Cancellation.IsCancellationRequested)
        { await request.ErrorAsync(409, "cancelled", "Operation cancelled"); }
        catch (InvalidOperationException ex)
        { Log.Debug(ex, "Automation operation refused"); await request.ErrorAsync(409, "invalid_state", "Operation refused in the current state; inspect diagnostics"); }
        catch (Exception ex) when (!request.Cancellation.IsCancellationRequested)
        { Log.Debug(ex, "Automation operation failed"); await request.ErrorAsync(500, "operation_failed", "Operation failed; inspect diagnostics"); }
    }
}
