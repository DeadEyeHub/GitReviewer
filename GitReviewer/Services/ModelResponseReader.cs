using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GitReviewer.Services;

public static class ModelResponseReader
{
    public static async Task<JsonDocument> ReadAsync(HttpContent content, Action<string>? log, CancellationToken token)
    {
        using var stream = await content.ReadAsStreamAsync(token);
        using var reader = new StreamReader(stream);
        var streaming = content.Headers.ContentType?.MediaType == "text/event-stream";
        var body = new StringBuilder();
        var line = new StringBuilder();
        var data = new StringBuilder();
        var text = new StringBuilder();
        var reasoning = new StringBuilder();
        var reasoningContent = new StringBuilder();
        var calls = new SortedDictionary<int, JsonObject>();
        string? finish = null;
        var done = false;
        var total = 0;
        var payloadSize = 0;
        void Event()
        {
            if (data.Length == 0) return;
            var value = data.ToString().TrimEnd('\n');
            data.Clear();
            if (done) throw new InvalidDataException("Data after SSE completion.");
            if (value == "[DONE]") { done = true; return; }
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.TryGetProperty("error", out _)) throw new InvalidDataException("Model stream returned an error; inspect Log.");
            foreach (var choice in document.RootElement.GetProperty("choices").EnumerateArray())
            {
                if (choice.GetProperty("index").GetInt32() != 0) throw new InvalidDataException("Unexpected model choice index.");
                if (finish is not null) throw new InvalidDataException("Model data after finish_reason.");
                var delta = choice.GetProperty("delta");
                foreach (var field in new[] { "content", "reasoning", "reasoning_content" })
                    if (delta.TryGetProperty(field, out var part) && part.ValueKind != JsonValueKind.Null)
                    {
                        var fragment = part.GetString()!;
                        payloadSize += fragment.Length;
                        log?.Invoke($"MODEL {field}: {fragment}");
                        if (field == "content") text.Append(fragment);
                        else if (field == "reasoning") reasoning.Append(fragment);
                        else reasoningContent.Append(fragment);
                    }
                if (delta.TryGetProperty("tool_calls", out var toolCalls))
                    foreach (var call in toolCalls.EnumerateArray())
                    {
                        var index = call.GetProperty("index").GetInt32();
                        if (index is < 0 or >= 64) throw new InvalidDataException("Invalid tool-call index.");
                        if (!calls.TryGetValue(index, out var target))
                            calls[index] = target = new JsonObject { ["id"] = "", ["type"] = "", ["function"] = new JsonObject { ["name"] = "", ["arguments"] = "" } };
                        foreach (var field in new[] { "id", "type" })
                            if (call.TryGetProperty(field, out var part)) target[field] = target[field]!.GetValue<string>() + part.GetString();
                        if (call.TryGetProperty("function", out var function))
                            foreach (var field in new[] { "name", "arguments" })
                                if (function.TryGetProperty(field, out var part))
                                {
                                    var fragment = part.GetString()!;
                                    payloadSize += fragment.Length;
                                    target["function"]![field] = target["function"]![field]!.GetValue<string>() + fragment;
                                }
                    }
                if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind != JsonValueKind.Null) finish = reason.GetString();
                if (payloadSize > 128_000) throw new InvalidDataException("Model response exceeds 128000 characters; review incomplete.");
            }
        }
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            // Retain received partial content even when canceled or malformed. Never log HTTP headers/URLs.
            log?.Invoke("HTTP response data: " + new string(buffer, 0, count));
            total += count;
            if (total > (streaming ? 4_000_000 : 128_000)) throw new InvalidDataException("Model HTTP response budget exhausted; review incomplete.");
            if (!streaming) { body.Append(buffer, 0, count); continue; }
            for (var i = 0; i < count; i++)
            {
                var c = buffer[i];
                if (c != '\n') { line.Append(c); continue; }
                var current = line.ToString().TrimEnd('\r');
                line.Clear();
                if (current.Length == 0) Event();
                else if (current.StartsWith("data:", StringComparison.Ordinal))
                {
                    var start = current.Length > 5 && current[5] == ' ' ? 6 : 5;
                    data.Append(current.AsSpan(start)).Append('\n');
                }
            }
        }
        token.ThrowIfCancellationRequested();
        if (!streaming)
        {
            var document = JsonDocument.Parse(body.ToString());
            LogReturnedMessage(document.RootElement, log);
            return document;
        }
        // SSE dispatches the final event at EOF even when the producer omits the last blank line.
        if (line.Length > 0)
        {
            var current = line.ToString().TrimEnd('\r');
            line.Clear();
            if (current.StartsWith("data:", StringComparison.Ordinal))
            {
                var start = current.Length > 5 && current[5] == ' ' ? 6 : 5;
                data.Append(current.AsSpan(start)).Append('\n');
            }
        }
        if (data.Length > 0) Event();
        if (!done || finish is null || line.Length != 0 || data.Length != 0)
            throw new InvalidDataException("Incomplete SSE response; review not saved.");
        if (!calls.Keys.SequenceEqual(Enumerable.Range(0, calls.Count))) throw new InvalidDataException("Missing tool-call index.");
        var message = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = text.ToString(),
            ["tool_calls"] = new JsonArray(calls.Values.Select(x => (JsonNode)x).ToArray())
        };
        if (reasoning.Length > 0) message["reasoning"] = reasoning.ToString();
        if (reasoningContent.Length > 0) message["reasoning_content"] = reasoningContent.ToString();
        return JsonDocument.Parse(new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject
            {
                ["finish_reason"] = finish,
                ["message"] = message
            })
        }.ToJsonString());
    }

    private static void LogReturnedMessage(JsonElement root, Action<string>? log)
    {
        if (log is null || !root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            return;
        var choice = choices[0];
        if (!choice.TryGetProperty("message", out var message)) return;
        foreach (var field in new[] { "reasoning", "reasoning_content", "content" })
            if (message.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String)
                log($"MODEL {field}: {value.GetString()}");
    }
}
