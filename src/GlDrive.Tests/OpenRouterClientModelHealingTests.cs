using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GlDrive.Spread;
using Xunit;

namespace GlDrive.Tests;

/// <summary>
/// AgentClient learned in July that OpenRouter retires ":free" slugs and answers 404
/// "use this slug instead: ...", and heals to the successor. Its sibling OpenRouterClient
/// (Server dialog AI Setup) never got the fix: on 2026-11-02 19:17 AI Setup for the new
/// server "sitea" sent openai/gpt-oss-120b:free, got that 404, and silently fell back to
/// the deterministic parser. It also logged the full provider envelope, including the
/// OpenRouter user_id, which AgentClient deliberately keeps out of the log.
/// </summary>
public sealed class OpenRouterClientModelHealingTests
{
    private const string Retired404 =
        """{"error":{"message":"This model is unavailable for free. The paid version is available now - use this slug instead: openai/gpt-oss-120b","code":404},"user_id":"user_SECRET"}""";

    private static readonly string Ok200 = JsonSerializer.Serialize(new
    {
        choices = new[] { new { message = new { content = """{"max_upload_slots": 3, "affils": ["GRP"]}""" } } }
    });

    private sealed class ScriptedHandler(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Models { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var model = doc.RootElement.GetProperty("model").GetString()!;
            Models.Add(model);
            return respond(model);
        }
    }

    // The real redactor still runs; only the live secret inventory (AppData + Credential Manager) is replaced.
    private static IEnumerable<string> NoSecrets() => [];

    private static HttpResponseMessage Reply(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static Task<AiSetupResult?> Analyze(OpenRouterClient client) =>
        client.AnalyzeSiteRules("rules", new Dictionary<string, string> { ["TV"] = "/tv" }, []);

    [Fact]
    public async Task Known_retired_free_slug_is_migrated_before_the_first_request()
    {
        var handler = new ScriptedHandler(_ => Reply(HttpStatusCode.OK, Ok200));
        using var client = new OpenRouterClient("key", "openai/gpt-oss-120b:free", handler, NoSecrets);

        var result = await Analyze(client);

        Assert.NotNull(result);
        Assert.Equal(["openai/gpt-oss-120b"], handler.Models);
    }

    [Fact]
    public async Task Retired_slug_404_retries_once_with_the_suggested_successor()
    {
        var handler = new ScriptedHandler(m => m == "vendor/old:free"
            ? Reply(HttpStatusCode.NotFound, Retired404.Replace("openai/gpt-oss-120b", "vendor/new"))
            : Reply(HttpStatusCode.OK, Ok200));
        using var client = new OpenRouterClient("key", "vendor/old:free", handler, NoSecrets);

        var result = await Analyze(client);

        Assert.NotNull(result);
        Assert.Equal(3, result!.MaxUploadSlots);
        Assert.Equal(["vendor/old:free", "vendor/new"], handler.Models);
    }

    [Fact]
    public async Task Other_failures_are_not_retried()
    {
        var handler = new ScriptedHandler(_ => Reply(HttpStatusCode.TooManyRequests, """{"error":{"message":"rate limited","code":429}}"""));
        using var client = new OpenRouterClient("key", "vendor/model", handler, NoSecrets);

        Assert.Null(await Analyze(client));
        Assert.Single(handler.Models);
    }

    [Fact]
    public async Task Only_a_404_is_treated_as_a_retired_slug()
    {
        // A 400 that echoes the phrase (e.g. a moderation message quoting user text) is not
        // a retirement notice; switching models on it would silently change provider.
        var handler = new ScriptedHandler(_ => Reply(HttpStatusCode.BadRequest,
            Retired404.Replace("\"code\":404", "\"code\":400")));
        using var client = new OpenRouterClient("key", "vendor/model", handler, NoSecrets);

        Assert.Null(await Analyze(client));
        Assert.Single(handler.Models);
    }

    [Fact]
    public async Task A_404_without_a_successor_is_not_retried()
    {
        var handler = new ScriptedHandler(_ => Reply(HttpStatusCode.NotFound, """{"error":{"message":"No endpoints found","code":404}}"""));
        using var client = new OpenRouterClient("key", "vendor/gone", handler, NoSecrets);

        Assert.Null(await Analyze(client));
        Assert.Single(handler.Models);
    }

    [Fact]
    public void Settings_no_longer_offer_the_retired_free_slug()
    {
        // Static scan of the list literal: constructing the view model needs a full AppConfig + WPF.
        var source = System.IO.File.ReadAllText(System.IO.Path.Combine(
            RepoRoot(), "src", "GlDrive", "UI", "SettingsViewModel.cs"));
        Assert.DoesNotContain("\"openai/gpt-oss-120b:free\"", source);
        Assert.Contains("\"openai/gpt-oss-120b\"", source);
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir != null; i++)
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(dir, "src", "GlDrive", "App.xaml.cs"))) return dir;
            dir = System.IO.Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException("Could not locate the repository root from " + AppContext.BaseDirectory);
    }
}
