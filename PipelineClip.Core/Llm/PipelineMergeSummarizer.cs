using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using PipelineClip.Core.Abstractions;

namespace PipelineClip.Core.Llm;

public sealed class PipelineMergeSummarizer(IHttpClientFactory factory, IOptions<PipelineMergeOptions> options)
    : IPipelineMergeSummarizer
{
    private readonly PipelineMergeOptions _o = options.Value;

    public async Task<string> MergeAsync(IReadOnlyList<JobAnalysis> successful, CancellationToken ct)
    {
        // tylko już zanonimizowane podsumowania, bez nazw jobów, URL-i i logów
        var userContent = string.Join("\n\n", successful.Select((a, i) => $"Podsumowanie {i + 1}:\n{a.Summary}"));
        var client = factory.CreateClient("PipelineMerge");
        var body = new JsonObject
        {
            ["model"] = _o.Model,
            ["temperature"] = _o.Temperature,
            ["max_tokens"] = _o.MaxTokens,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = _o.SystemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = userContent }),
        };

        HttpResponseMessage resp;
        try
        {
            resp = await SendAsync(client, body, ct);
            if (resp.StatusCode == (HttpStatusCode)429 || (int)resp.StatusCode >= 500)
            {
                resp.Dispose();
                resp = await SendAsync(client, body, ct);
            }
        }
        catch (HttpRequestException e)
        {
            throw new LlmException($"Błąd połączenia z LLM: {e.GetType().Name}");
        }

        using (resp)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new LlmException($"LLM zwrócił {(int)resp.StatusCode}: {Truncate(text)}");

            string? content;
            try
            {
                content = JsonNode.Parse(text)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or ArgumentOutOfRangeException)
            {
                throw new LlmException($"Niepoprawna odpowiedź LLM: {Truncate(text)}");
            }

            if (string.IsNullOrWhiteSpace(content)) throw new LlmException("Pusta odpowiedź modelu");
            return content;
        }
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, JsonObject body, CancellationToken ct) =>
        client.PostAsJsonAsync("chat/completions", body, ct);

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300];
}
