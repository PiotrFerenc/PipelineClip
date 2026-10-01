using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using PipelineClip.Core;
using PipelineClip.Core.Abstractions;
using PipelineClip.Core.Llm;

namespace PipelineClip.Tests.Llm;

public class TestStageSummarizerTests
{
    private const string Key = "sk-secret-123";

    private sealed class Stub(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Req, string? Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var body = r.Content is null ? null : await r.Content.ReadAsStringAsync(ct);
            Calls.Add((r, body));
            return respond(r, body);
        }
    }

    private sealed class Factory(Stub stub, TestStageAnalysisOptions o) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal("TestStageAnalysis", name);
            var c = new HttpClient(stub, false);
            HttpClientHeaders.Apply(c, o);
            return c;
        }
    }

    private static TestStageAnalysisOptions Opts() => new()
    {
        BaseAddress = "https://llm.example.com/v1/",
        Model = "m-1",
        SystemPrompt = "SYS",
        Temperature = 0.3,
        MaxTokens = 123,
        ApiKey = Key,
        Headers = { ["Authorization"] = "Bearer {ApiKey}", ["x-api-key"] = "{ApiKey}" },
    };

    private static HttpResponseMessage Json(string s, HttpStatusCode c = HttpStatusCode.OK) =>
        new(c) { Content = new StringContent(s, Encoding.UTF8, "application/json") };

    private static string Ok(string content) =>
        System.Text.Json.JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } });

    private static (TestStageSummarizer S, Stub Stub) Make(Func<HttpRequestMessage, string?, HttpResponseMessage> f)
    {
        var o = Opts();
        var stub = new Stub(f);
        return (new TestStageSummarizer(new Factory(stub, o), Options.Create(o)), stub);
    }

    [Fact]
    public async Task SendsExpectedBodyAndParsesContent()
    {
        var (s, stub) = Make((_, _) => Json(Ok("wynik")));
        Assert.Equal("wynik", await s.SummarizeAsync("USER", default));

        var (req, body) = Assert.Single(stub.Calls);
        Assert.Equal("https://llm.example.com/v1/chat/completions", req.RequestUri!.ToString());
        var j = System.Text.Json.JsonDocument.Parse(body!).RootElement;
        Assert.Equal("m-1", j.GetProperty("model").GetString());
        Assert.Equal(0.3, j.GetProperty("temperature").GetDouble());
        Assert.Equal(123, j.GetProperty("max_tokens").GetInt32());
        var m = j.GetProperty("messages");
        Assert.Equal("system", m[0].GetProperty("role").GetString());
        Assert.Equal("SYS", m[0].GetProperty("content").GetString());
        Assert.Equal("user", m[1].GetProperty("role").GetString());
        Assert.Equal("USER", m[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task HeadersHaveApiKeySubstituted()
    {
        var (s, stub) = Make((_, _) => Json(Ok("x")));
        await s.SummarizeAsync("u", default);
        var h = stub.Calls[0].Req.Headers;
        Assert.Equal($"Bearer {Key}", h.GetValues("Authorization").Single());
        Assert.Equal(Key, h.GetValues("x-api-key").Single());
    }

    [Fact]
    public void SectionBindsToOptions()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TestStageAnalysis:BaseAddress"] = "https://x/v1/",
            ["TestStageAnalysis:Model"] = "custom-model",
            ["TestStageAnalysis:SystemPrompt"] = "custom prompt",
            ["TestStageAnalysis:Headers:api-key"] = "{ApiKey}",
        }).Build();
        var o = cfg.GetSection("TestStageAnalysis").Get<TestStageAnalysisOptions>()!;
        Assert.Equal("custom-model", o.Model);
        Assert.Equal("custom prompt", o.SystemPrompt);
        Assert.NotEqual(new TestStageAnalysisOptions().SystemPrompt, o.SystemPrompt);
        Assert.Equal("{ApiKey}", o.Headers["api-key"]);
    }

    [Theory]
    [InlineData("{\"choices\":[]}")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"\"}}]}")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":null}}]}")]
    [InlineData("not json")]
    [InlineData("")]
    public async Task EmptyOrBadResponseThrows(string body)
    {
        var (s, _) = Make((_, _) => Json(body));
        await Assert.ThrowsAsync<LlmException>(() => s.SummarizeAsync("u", default));
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public async Task RetriesOnceThenSucceeds(int code)
    {
        var n = 0;
        var (s, stub) = Make((_, _) => ++n == 1 ? Json("busy", (HttpStatusCode)code) : Json(Ok("ok")));
        Assert.Equal("ok", await s.SummarizeAsync("u", default));
        Assert.Equal(2, stub.Calls.Count);
    }

    [Fact]
    public async Task PersistentServerErrorThrowsAfterOneRetry()
    {
        var (s, stub) = Make((_, _) => Json(new string('e', 1000), HttpStatusCode.InternalServerError));
        var ex = await Assert.ThrowsAsync<LlmException>(() => s.SummarizeAsync("u", default));
        Assert.Equal(2, stub.Calls.Count);
        Assert.Contains("500", ex.Message);
        Assert.True(ex.Message.Length < 400);
    }

    [Fact]
    public async Task UnauthorizedDoesNotRetryAndNeverLeaksKey()
    {
        var (s, stub) = Make((r, _) => Json("bad key", HttpStatusCode.Unauthorized));
        var ex = await Assert.ThrowsAsync<LlmException>(() => s.SummarizeAsync("u", default));
        Assert.Single(stub.Calls);
        Assert.Contains("401", ex.Message);
        Assert.DoesNotContain(Key, ex.Message);
    }

    [Fact]
    public async Task OtherExceptionMessagesDoNotContainKey()
    {
        foreach (var body in new[] { "", "garbage", "{\"choices\":[]}" })
        {
            var (s, _) = Make((_, _) => Json(body));
            var ex = await Assert.ThrowsAsync<LlmException>(() => s.SummarizeAsync("u", default));
            Assert.DoesNotContain(Key, ex.Message);
        }
        var (s2, _) = Make((_, _) => throw new HttpRequestException($"fail {Key}"));
        var ex2 = await Assert.ThrowsAsync<LlmException>(() => s2.SummarizeAsync("u", default));
        Assert.DoesNotContain(Key, ex2.Message);
    }
}
