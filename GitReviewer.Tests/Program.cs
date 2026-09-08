using System.Net;
using System.Text.Json;
using GitReviewer.Models;
using GitReviewer.Services;

var passed = 0;
foreach (var prefix in new[] { "", "/proxy" })
foreach (var suffix in new[] { "/v1", "/v1/", "/v1/models", "/v1/models/", "/v1/chat/completions" })
{
    var endpoint = "https://server.example" + prefix + suffix + "?tenant=test";
    Equal("https://server.example" + prefix + "/v1/chat/completions?tenant=test",
        ModelClient.ResolveEndpoint(endpoint).AbsoluteUri);
    Equal("https://server.example" + prefix + "/v1/models?tenant=test",
        ModelClient.ResolveEndpoint(endpoint, true).AbsoluteUri);
}
foreach (var path in new[] { "/custom/generate", "/api/chat", "/chat/completions/" })
{
    var endpoint = "https://server.example" + path + "?version=1";
    Equal(endpoint, ModelClient.ResolveEndpoint(endpoint).AbsoluteUri);
}
await Throws<InvalidOperationException>(() => Task.FromResult(ModelClient.ResolveEndpoint("file:///tmp/model")));
await Throws<InvalidOperationException>(() => Task.FromResult(ModelClient.ResolveEndpoint("not a URL")));
await Throws<InvalidOperationException>(() => Task.FromResult(ModelClient.ResolveEndpoint("https://server.example/custom", true)));

using (var http = new HttpClient(new FakeHandler((request, _) =>
{
    Equal("https://server.example/custom/generate?version=1", request.RequestUri!.AbsoluteUri);
    Equal(HttpMethod.Post, request.Method);
    return Task.FromResult(Json("""{"choices":[{"message":{"content":"OK"}}]}"""));
})))
{
    await new ModelClient(http).TestConnectionAsync(new ModelProfile
    {
        Endpoint = "https://server.example/custom/generate?version=1",
        Model = "existing-profile-model"
    }, CancellationToken.None);
}

var variable = "GITREVIEWER_TEST_KEY_" + Guid.NewGuid().ToString("N");
try
{
    foreach (var environmentKey in new string?[] { "environment-secret", null, " " })
    foreach (var storedKey in new[] { "stored-secret", "" })
    {
        Environment.SetEnvironmentVariable(variable, environmentKey);
        var profile = new ModelProfile
        {
            Endpoint = "https://server.example/proxy/v1/models?tenant=test",
            ApiKey = storedKey,
            ApiKeyEnvironment = variable
        };
        var expectedKey = string.IsNullOrWhiteSpace(environmentKey) ? storedKey : environmentKey;
        using var http = new HttpClient(new FakeHandler(async (request, token) =>
        {
            Equal(expectedKey.Length == 0 ? null : "Bearer " + expectedKey, request.Headers.Authorization?.ToString());
            if (request.Method == HttpMethod.Get)
            {
                Equal(profile.Endpoint, request.RequestUri!.AbsoluteUri);
                Equal<HttpContent?>(null, request.Content);
                return Json("""{"data":[{"id":"served-alias","max_model_len":32768},{"id":"served-alias"},{"id":"other"},{"id":"unknown-limit","max_model_len":"invalid"},{"id":""}]}""");
            }
            Equal(HttpMethod.Post, request.Method);
            Equal("https://server.example/proxy/v1/chat/completions?tenant=test", request.RequestUri!.AbsoluteUri);
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Equal("manual-alias", payload.RootElement.GetProperty("model").GetString());
            Equal(false, payload.RootElement.TryGetProperty("max_model_len", out _));
            Equal(2, payload.RootElement.GetProperty("messages").GetArrayLength());
            return Json("""{"choices":[{"finish_reason":"stop","message":{"content":"OK"}}]}""");
        }));
        var client = new ModelClient(http);
        var models = await client.DiscoverModelsAsync(profile, CancellationToken.None);
        Equal(3, models.Count);
        Equal("served-alias", models[0].Id);
        Equal<long?>(32768, models[0].MaxModelLength);
        Equal<long?>(null, models[1].MaxModelLength);
        Equal<long?>(null, models[2].MaxModelLength);
        Equal("", profile.Model);
        profile.Model = "manual-alias";
        await client.TestConnectionAsync(profile, CancellationToken.None);
    }
}
finally
{
    Environment.SetEnvironmentVariable(variable, null);
}

foreach (var language in new[] { "en", "ru" })
{
    Localization.SetLanguage(language);
    foreach (var body in new[] { "not json", "{}", "{\"data\":{}}", "{\"data\":[{}]}" })
    {
        using var http = new HttpClient(new FakeHandler((_, _) => Task.FromResult(Json(body))));
        await Throws<InvalidDataException>(() => new ModelClient(http).DiscoverModelsAsync(
            new ModelProfile { Endpoint = "https://server.example/v1" }, CancellationToken.None));
    }
    using var errorHttp = new HttpClient(new FakeHandler((_, _) => Task.FromResult(
        new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("denied") })));
    await Throws<HttpRequestException>(() => new ModelClient(errorHttp).DiscoverModelsAsync(
        new ModelProfile { Endpoint = "https://server.example/v1" }, CancellationToken.None));
}
using (var http = new HttpClient(new FakeHandler((_, _) => Task.FromResult(Json("{\"data\":[]}")))))
{
    var client = new ModelClient(http);
    var profile = new ModelProfile { Endpoint = "https://server.example/v1" };
    Equal(0, (await client.DiscoverModelsAsync(profile, CancellationToken.None)).Count);
    await Throws<InvalidOperationException>(() => client.TestConnectionAsync(profile, CancellationToken.None));
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    await Throws<OperationCanceledException>(() => client.DiscoverModelsAsync(profile, cancellation.Token));
}
Console.WriteLine($"Passed {passed} assertions.");

void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected '{expected}', received '{actual}'.");
    passed++;
}

async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { passed++; return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}

static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return send(request, cancellationToken);
    }
}
