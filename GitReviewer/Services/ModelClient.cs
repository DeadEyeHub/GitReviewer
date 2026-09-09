using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using GitReviewer.Models;

namespace GitReviewer.Services;

public sealed class ModelClient
{
    private readonly HttpClient _httpClient;

    public ModelClient(HttpClient? httpClient = null) =>
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

    public sealed record AvailableModel(string Id, long? MaxModelLength);

    public async Task<IReadOnlyList<AvailableModel>> DiscoverModelsAsync(
        ModelProfile profile, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ResolveEndpoint(profile.Endpoint, true));
        var apiKey = ResolveApiKey(profile);
        if (apiKey.Length > 0)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(Localization.Format(
                "API returned {0}: {1}", "API вернул {0}: {1}",
                (int)response.StatusCode, body[..Math.Min(body.Length, 1000)]));
        try
        {
            using var document = JsonDocument.Parse(body);
            var models = new List<AvailableModel>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in document.RootElement.GetProperty("data").EnumerateArray())
            {
                var id = item.GetProperty("id").GetString();
                if (string.IsNullOrWhiteSpace(id) || !ids.Add(id))
                    continue;
                long? maxLength = item.TryGetProperty("max_model_len", out var length) &&
                                  length.ValueKind == JsonValueKind.Number &&
                                  length.TryGetInt64(out var value) && value > 0 ? value : null;
                models.Add(new AvailableModel(id, maxLength));
            }
            return models;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidDataException(Localization.Text(
                "The API returned an unsupported model list format.",
                "API вернул список моделей неподдерживаемого формата."), exception);
        }
    }

    public async Task<string> TestConnectionAsync(ModelProfile profile, CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        var response = await SendAsync(profile,
            Localization.Text(
                "You are checking whether the model connection works.",
                "Ты проверяешь доступность подключения к модели."),
            Localization.Text("Reply with one word: OK", "Ответь одним словом: OK"),
            cancellationToken);
        var elapsed = DateTime.UtcNow - started;
        return Localization.Format(
            "Connection successful. Response: {0} ({1:F1} sec.)",
            "Подключение успешно. Ответ: {0} ({1:F1} сек.)",
            response.Trim(), elapsed.TotalSeconds);
    }

    public Task<string> ReviewAsync(
        ModelProfile profile,
        CommitInfo commit,
        string diff,
        string systemPrompt,
        CancellationToken cancellationToken,
        Action<ReviewStage>? progress = null)
    {
        var userPrompt = new StringBuilder()
            .AppendLine(Localization.Text(
                "Review only the changes introduced by this commit.",
                "Проверь только изменения, внесенные этим коммитом."))
            .AppendLine($"Commit: {commit.Sha}")
            .AppendLine($"Author: {commit.Author}")
            .AppendLine($"Date: {commit.Date:O}")
            .AppendLine($"Message: {commit.Subject}")
            .AppendLine()
            .AppendLine(Localization.Text(
                "Return plain text. If there are no bugs, return only NO_BUGS.",
                "Верни простой текст. Если ошибок нет, верни только NO_BUGS."))
            .AppendLine(Localization.Text(
                "Use a separate block for each potential bug:",
                "Используй отдельный блок для каждой потенциальной ошибки:"))
            .AppendLine("BUG")
            .AppendLine(Localization.Text("FILE: file path", "FILE: путь к файлу"))
            .AppendLine(Localization.Text("LINE: line number", "LINE: номер строки"))
            .AppendLine(Localization.Text("SIDE: NEW, OLD, or HUNK", "SIDE: NEW, OLD или HUNK"))
            .AppendLine(Localization.Text(
                "DESCRIPTION: a concise explanation of the bug and when it occurs",
                "DESCRIPTION: краткое объяснение ошибки и условий ее проявления"))
            .AppendLine("END")
            .AppendLine()
            .AppendLine(Localization.Text(
                "Use SIDE=OLD for deleted lines. If there is no exact line, use SIDE=HUNK and the nearest line.",
                "Используй SIDE=OLD для удаленных строк. Если точной строки нет, используй SIDE=HUNK и ближайшую строку."))
            .AppendLine(Localization.Text(
                "Write DESCRIPTION in English. Do not use JSON or Markdown.",
                "Пиши DESCRIPTION на русском языке. Не используй JSON или Markdown."))
            .AppendLine()
            .AppendLine("DIFF:")
            .Append(diff)
            .ToString();

        return SendAsync(profile, systemPrompt, userPrompt, cancellationToken, progress);
    }

    private async Task<string> SendAsync(
        ModelProfile profile,
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken,
        Action<ReviewStage>? progress = null)
    {
        ValidateProfile(profile);
        var payload = new
        {
            model = profile.Model,
            temperature = 0,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, ResolveEndpoint(profile.Endpoint))
        {
            Content = JsonContent.Create(payload)
        };
        var apiKey = ResolveApiKey(profile);
        if (apiKey.Length > 0)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        progress?.Invoke(ReviewStage.Request);
        var responseTask = _httpClient.SendAsync(request, cancellationToken);
        progress?.Invoke(ReviewStage.Waiting);
        using var response = await responseTask;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        progress?.Invoke(ReviewStage.Response);
        if (!response.IsSuccessStatusCode)
        {
            var detail = body.Length > 1000 ? body[..1000] : body;
            throw new HttpRequestException(Localization.Format(
                "API returned {0}: {1}",
                "API вернул {0}: {1}",
                (int)response.StatusCode, detail));
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var choice = document.RootElement.GetProperty("choices")[0];
            if (choice.TryGetProperty("finish_reason", out var finishReasonElement))
            {
                var finishReason = finishReasonElement.GetString();
                if (!string.IsNullOrWhiteSpace(finishReason) &&
                    !finishReason.Equals("stop", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(Localization.Format(
                        "The model response is incomplete: finish_reason={0}.",
                        "Ответ модели не завершен: finish_reason={0}.",
                        finishReason));
            }

            var content = choice.GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(content))
                throw new InvalidDataException(Localization.Text(
                    "The model returned an empty response.",
                    "Модель вернула пустой ответ."));
            return content;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidDataException(Localization.Text(
                "The API returned an unsupported response format.",
                "API вернул ответ неподдерживаемого формата."), exception);
        }
    }

    private static void ValidateProfile(ModelProfile profile)
    {
        ResolveEndpoint(profile.Endpoint);
        if (string.IsNullOrWhiteSpace(profile.Model))
            throw new InvalidOperationException(Localization.Text(
                "Enter a model name.",
                "Укажите название модели."));
    }

    public static Uri ResolveEndpoint(string text, bool models = false)
    {
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var endpoint))
            throw new InvalidOperationException(Localization.Text(
                "Enter a valid model endpoint.",
                "Укажите корректный endpoint модели."));
        if (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException(Localization.Text(
                "The model endpoint must use HTTP or HTTPS.",
                "Endpoint модели должен использовать HTTP или HTTPS."));
        var path = endpoint.AbsolutePath.TrimEnd('/');
        string basePath;
        if (path.EndsWith("/chat/completions", StringComparison.Ordinal))
            basePath = path[..^"/chat/completions".Length];
        else if (path.EndsWith("/v1/models", StringComparison.Ordinal))
            basePath = path[..^"/models".Length];
        else if (path.EndsWith("/v1", StringComparison.Ordinal))
            basePath = path;
        else
        {
            if (!models)
                return endpoint; // Existing custom full endpoints are used verbatim.
            throw new InvalidOperationException(Localization.Text(
                "Model discovery requires a /v1, /v1/models, or /chat/completions endpoint. Enter the model manually for custom endpoints.",
                "Для загрузки моделей нужен endpoint /v1, /v1/models или /chat/completions. Для нестандартных адресов введите модель вручную."));
        }
        if (!models && path.EndsWith("/chat/completions", StringComparison.Ordinal))
            return endpoint;
        return new UriBuilder(endpoint) { Path = basePath + (models ? "/models" : "/chat/completions") }.Uri;
    }

    private static string ResolveApiKey(ModelProfile profile)
    {
        if (profile.ApiKeyEnvironment.Length > 0)
        {
            var environmentValue = Environment.GetEnvironmentVariable(profile.ApiKeyEnvironment);
            if (!string.IsNullOrWhiteSpace(environmentValue))
                return environmentValue;
        }

        return profile.ApiKey;
    }
}
