using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using PipelineClip.Core;
using PipelineClip.Core.Abstractions;
using PipelineClip.Core.GitLab;

namespace PipelineClip.Tests.GitLab;

public class GitLabClientTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal("GitLab", name);
            return new HttpClient(handler, false) { BaseAddress = new Uri("https://gitlab.test/api/v4/") };
        }
    }

    private static GitLabClient Create(StubHandler h, bool allowed = false, int maxBytes = 1000) =>
        new(new StubFactory(h),
            Options.Create(new GitLabOptions { IncludeAllowedFailures = allowed, ApiKey = "SECRET-TOKEN" }),
            Options.Create(new AnalysisOptions { MaxLogBytes = maxBytes }),
            TimeSpan.Zero);

    private static HttpResponseMessage Json(string body, string? next = null)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (next is not null) r.Headers.Add("X-Next-Page", next);
        return r;
    }

    private static string Job(int id, bool allow = false) =>
        $$"""{"id":{{id}},"name":"j{{id}}","stage":"test","web_url":"http://x/{{id}}","allow_failure":{{(allow ? "true" : "false")}}}""";

    [Fact]
    public async Task Paginates_ByNextPageHeader()
    {
        var h = new StubHandler(r => r.RequestUri!.Query.Contains("&page=1")
            ? Json($"[{Job(1)}]", "2")
            : Json($"[{Job(2)}]", ""));
        var jobs = await Create(h).GetFailedJobsAsync("5", 10, default);
        Assert.Equal([1L, 2L], jobs.Select(j => j.Id));
        Assert.Equal(2, h.Requests.Count);
        Assert.Contains("scope[]=failed", h.Requests[0].RequestUri!.ToString());
        Assert.Contains("per_page=100", h.Requests[0].RequestUri!.ToString());
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public async Task FiltersAllowFailure(bool include, int expected)
    {
        var h = new StubHandler(_ => Json($"[{Job(1)},{Job(2, true)}]"));
        var jobs = await Create(h, include).GetFailedJobsAsync("5", 10, default);
        Assert.Equal(expected, jobs.Count);
    }

    [Fact]
    public async Task NotFound_MapsMessage()
    {
        var h = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var ex = await Assert.ThrowsAsync<GitLabException>(() => Create(h).GetFailedJobsAsync("5", 10, default));
        Assert.Equal(404, ex.StatusCode);
        Assert.Equal("Nie znaleziono pipeline 10 w projekcie 5", ex.Message);
        Assert.DoesNotContain("SECRET-TOKEN", ex.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Unauthorized_MapsMessage(HttpStatusCode code)
    {
        var h = new StubHandler(_ => new HttpResponseMessage(code));
        var ex = await Assert.ThrowsAsync<GitLabException>(() => Create(h).GetFailedJobsAsync("5", 10, default));
        Assert.Equal((int)code, ex.StatusCode);
        Assert.Contains("read_api", ex.Message);
        Assert.DoesNotContain("SECRET-TOKEN", ex.Message);
    }

    [Fact]
    public async Task Retries_Once_On429_ThenSucceeds()
    {
        var calls = 0;
        var h = new StubHandler(_ => ++calls == 1 ? new HttpResponseMessage((HttpStatusCode)429) : Json("[]"));
        Assert.Empty(await Create(h).GetFailedJobsAsync("5", 10, default));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Second429_Throws()
    {
        var h = new StubHandler(_ => new HttpResponseMessage((HttpStatusCode)429));
        var ex = await Assert.ThrowsAsync<GitLabException>(() => Create(h).GetFailedJobsAsync("5", 10, default));
        Assert.Equal(429, ex.StatusCode);
        Assert.Equal(2, h.Requests.Count);
    }

    [Fact]
    public async Task Trace_LargerThanMax_ReturnsTailWithoutPartialLine()
    {
        var log = string.Join("\n", Enumerable.Range(0, 100).Select(i => $"line{i:D3}")) + "\n"; // 8 B/line
        var h = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(log)) });
        var text = await Create(h, maxBytes: 100).GetJobTraceAsync("5", 1, default);
        Assert.StartsWith("line", text);
        Assert.EndsWith("line099\n", text);
        Assert.True(Encoding.UTF8.GetByteCount(text) <= 100);
        Assert.EndsWith(text, log);
    }

    [Fact]
    public async Task Trace_SmallerThanMax_ReturnedWhole()
    {
        var h = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("a\nb\n") });
        Assert.Equal("a\nb\n", await Create(h).GetJobTraceAsync("5", 1, default));
    }

    [Fact]
    public async Task ProjectIdWithSlash_IsEncoded()
    {
        var h = new StubHandler(_ => Json("[]"));
        await Create(h).GetFailedJobsAsync("group/proj", 10, default);
        Assert.Contains("projects/group%2Fproj/", h.Requests[0].RequestUri!.AbsoluteUri);
    }
}
