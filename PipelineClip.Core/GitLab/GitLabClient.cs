using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PipelineClip.Core.Abstractions;

namespace PipelineClip.Core.GitLab;

public sealed class GitLabClient : IGitLabClient
{
    private const int MaxPages = 20;
    private readonly IHttpClientFactory _factory;
    private readonly GitLabOptions _gitLab;
    private readonly AnalysisOptions _analysis;
    private readonly TimeSpan _defaultRetryDelay;

    /// <param name="defaultRetryDelay">Opóźnienie retry bez Retry-After (domyślnie 2 s); wstrzykiwalne dla testów.</param>
    public GitLabClient(
        IHttpClientFactory factory,
        IOptions<GitLabOptions> gitLab,
        IOptions<AnalysisOptions> analysis,
        TimeSpan? defaultRetryDelay = null)
    {
        _factory = factory;
        _gitLab = gitLab.Value;
        _analysis = analysis.Value;
        _defaultRetryDelay = defaultRetryDelay ?? TimeSpan.FromSeconds(2);
    }

    public async Task<IReadOnlyList<FailedJob>> GetFailedJobsAsync(string projectId, long pipelineId, CancellationToken ct)
    {
        var result = new List<FailedJob>();
        var project = Uri.EscapeDataString(projectId);
        string? page = "1";
        for (var i = 0; i < MaxPages && !string.IsNullOrEmpty(page); i++)
        {
            var url = $"projects/{project}/pipelines/{pipelineId}/jobs?scope[]=failed&per_page=100&page={page}";
            using var response = await SendAsync(url, null, HttpCompletionOption.ResponseContentRead, projectId, pipelineId, ct);
            var jobs = await response.Content.ReadFromJsonAsyncCompat<List<JobDto>>(ct) ?? [];
            result.AddRange(jobs
                .Where(j => _gitLab.IncludeAllowedFailures || !j.AllowFailure)
                .Select(j => new FailedJob(j.Id, j.Name ?? "", j.Stage ?? "", j.WebUrl ?? "", j.AllowFailure)));
            page = response.Headers.TryGetValues("X-Next-Page", out var v) ? v.FirstOrDefault() : null;
        }
        return result;
    }

    public async Task<string> GetJobTraceAsync(string projectId, long jobId, CancellationToken ct)
    {
        var max = _analysis.MaxLogBytes;
        var url = $"projects/{Uri.EscapeDataString(projectId)}/jobs/{jobId}/trace";
        using var response = await SendAsync(url, max, HttpCompletionOption.ResponseHeadersRead, projectId, null, ct);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) return "";

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var ring = new byte[max];
        long total = 0;
        var chunk = new byte[81920];
        int n;
        while ((n = await stream.ReadAsync(chunk, ct)) > 0)
        {
            for (var offset = 0; offset < n;)
            {
                var pos = (int)(total % max);
                var len = Math.Min(n - offset, max - pos);
                Buffer.BlockCopy(chunk, offset, ring, pos, len);
                offset += len;
                total += len;
            }
        }

        // Serwer mógł sam obciąć (206) albo my (total > max): pierwsza linia może być ucięta.
        var truncated = total > max || response.StatusCode == HttpStatusCode.PartialContent;
        byte[] tail;
        if (total <= max) tail = ring.AsSpan(0, (int)total).ToArray();
        else
        {
            var start = (int)(total % max);
            tail = new byte[max];
            Buffer.BlockCopy(ring, start, tail, 0, max - start);
            Buffer.BlockCopy(ring, 0, tail, max - start, start);
        }

        var text = Encoding.UTF8.GetString(tail);
        if (!truncated) return text;
        var nl = text.IndexOf('\n');
        return nl < 0 ? "" : text[(nl + 1)..];
    }

    private async Task<HttpResponseMessage> SendAsync(
        string url, int? tailBytes, HttpCompletionOption option, string projectId, long? pipelineId, CancellationToken ct)
    {
        var client = _factory.CreateClient("GitLab");
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (tailBytes is { } b) request.Headers.Range = new RangeHeaderValue(null, b);
            var response = await client.SendAsync(request, option, ct);
            var code = (int)response.StatusCode;
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                return response;

            var retryAfter = response.Headers.RetryAfter?.Delta
                             ?? (response.Headers.RetryAfter?.Date is { } d ? d - DateTimeOffset.UtcNow : null);
            response.Dispose();

            if ((code == 429 || code >= 500) && attempt == 0)
            {
                var delay = retryAfter is { } r && r >= TimeSpan.Zero ? r : _defaultRetryDelay;
                await Task.Delay(delay, ct);
                continue;
            }

            throw code switch
            {
                401 or 403 => new GitLabException(code, "Token odrzucony lub bez uprawnień `read_api`"),
                404 => new GitLabException(code, pipelineId is { } id
                    ? $"Nie znaleziono pipeline {id} w projekcie {projectId}"
                    : $"Nie znaleziono zasobu w projekcie {projectId}"),
                _ => new GitLabException(code, $"GitLab zwrócił błąd HTTP {code}"),
            };
        }
    }

    private sealed class JobDto
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("stage")] public string? Stage { get; set; }
        [JsonPropertyName("web_url")] public string? WebUrl { get; set; }
        [JsonPropertyName("allow_failure")] public bool AllowFailure { get; set; }
    }
}

internal static class HttpContentJson
{
    public static async Task<T?> ReadFromJsonAsyncCompat<T>(this HttpContent content, CancellationToken ct)
    {
        await using var s = await content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<T>(s, cancellationToken: ct);
    }
}
