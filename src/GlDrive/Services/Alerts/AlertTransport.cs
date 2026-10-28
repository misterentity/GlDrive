using System.Net;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using GlDrive.Config;

namespace GlDrive.Services.Alerts;

internal sealed class AlertDeliveryFailure(string failureClass, bool retryable, TimeSpan? retryAfter = null)
    : Exception("Alert delivery failed")
{
    internal string FailureClass { get; } = failureClass;
    internal bool Retryable { get; } = retryable;
    internal TimeSpan? RetryAfter { get; } = retryAfter;

    internal static AlertDeliveryFailure Classify(Exception exception) => exception switch
    {
        AlertDeliveryFailure failure => failure,
        OperationCanceledException => new("timeout", true),
        HttpRequestException or IOException => new("connection", true),
        SmtpException => new("smtp", true),
        _ => new("configuration", false)
    };
}

/// <summary>All addressing and authentication lives in the per-target Credential Manager JSON.</summary>
internal sealed class AlertTransport : IDisposable
{
    private readonly HttpClient _http;
    private readonly Func<string, string?> _credentials;
    internal AlertTransport(HttpMessageHandler? handler = null, Func<string, string?>? credentials = null)
    {
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
        _credentials = credentials ?? CredentialStore.GetApiKey;
    }

    internal async Task SendAsync(AlertTargetConfig target, string body, CancellationToken ct)
    {
        var raw = _credentials("alerts-" + target.Id);
        if (string.IsNullOrEmpty(raw)) throw new AlertDeliveryFailure("unconfigured", false);
        using var document = JsonDocument.Parse(raw);
        var data = document.RootElement;
        string Field(string name) => data.TryGetProperty(name, out var field) ? field.GetString() ?? "" : "";
        string Required(string name) => Field(name) is { Length: > 0 } text ? text : throw new InvalidOperationException("Alert target is incomplete");
        Uri Https(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0)
                throw new InvalidOperationException("Alert target requires HTTPS");
            return uri;
        }
        if (target.Kind == "smtp")
        {
            using var smtp = new SmtpClient(Required("host"), data.TryGetProperty("port", out var port) ? port.GetInt32() : 587)
            { EnableSsl = true, Credentials = new NetworkCredential(Required("username"), Required("password")) };
            using var mail = new MailMessage(Required("from"), Required("to"), "GlDrive morning digest", body);
            await smtp.SendMailAsync(mail, ct).ConfigureAwait(false);
            return;
        }
        using var request = target.Kind switch
        {
            "ntfy" => new HttpRequestMessage(HttpMethod.Post, Https(Required("url"))) { Content = new StringContent(body, Encoding.UTF8, "text/plain") },
            "pushover" => new HttpRequestMessage(HttpMethod.Post, "https://api.pushover.net/1/messages.json")
            { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = Required("token"), ["user"] = Required("user"), ["message"] = body }) },
            "telegram" => new HttpRequestMessage(HttpMethod.Post, "https://api.telegram.org/bot" + Uri.EscapeDataString(Required("token")) + "/sendMessage")
            { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["chat_id"] = Required("chatId"), ["text"] = body }) },
            "discord" => new HttpRequestMessage(HttpMethod.Post, Https(Required("url")))
            { Content = new StringContent(JsonSerializer.Serialize(new { content = body, allowed_mentions = new { parse = Array.Empty<string>() } }), Encoding.UTF8, "application/json") },
            "heartbeat" => new HttpRequestMessage(HttpMethod.Post, Https(Required("url"))) { Content = new StringContent(body, Encoding.UTF8, "application/json") },
            _ => throw new InvalidOperationException("Unknown alert target")
        };
        if (target.Kind == "ntfy" && Field("token").Length > 0)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Field("token"));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            var retryAfter = response.Headers.RetryAfter?.Delta ??
                (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : (TimeSpan?)null);
            throw new AlertDeliveryFailure("http-" + status, status is 408 or 429 || status >= 500,
                retryAfter > TimeSpan.Zero ? retryAfter : null);
        }
    }

    public void Dispose() => _http.Dispose();
}
