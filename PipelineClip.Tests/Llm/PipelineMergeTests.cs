using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PipelineClip.Cli;
using PipelineClip.Core;
using PipelineClip.Core.Abstractions;
using PipelineClip.Core.Llm;
using PipelineClip.Core.Orchestration;
using PipelineClip.Core.Strategies;

namespace PipelineClip.Tests.Llm;

public class PipelineMergeTests
{
    private sealed class FakeGitLab(params FailedJob[] jobs) : IGitLabClient
    {
        public Task<IReadOnlyList<FailedJob>> GetFailedJobsAsync(string p, long id, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<FailedJob>>(jobs);
        public Task<string> GetJobTraceAsync(string p, long jobId, CancellationToken ct) => Task.FromResult("");
    }

    private sealed class FakeStage(Func<FailedJob, JobAnalysis> f) : IStageAnalizator
    {
        public string Stage => "test";
        public Task<JobAnalysis> AnalyzeAsync(string p, FailedJob job, CancellationToken ct) => Task.FromResult(f(job));
    }

    private sealed class FakeMerge(Func<string>? f = null) : IPipelineMergeSummarizer
    {
        public int Calls;
        public IReadOnlyList<JobAnalysis>? Input;
        public Task<string> MergeAsync(IReadOnlyList<JobAnalysis> s, CancellationToken ct)
        {
            Calls++; Input = s;
            return Task.FromResult(f is null ? "scalone" : f());
        }
    }

    private static FailedJob Job(long id) => new(id, $"job{id}", "test", "", false);

    private static JobAnalysis Ok(FailedJob j) => new(j, true, $"sum{j.Id}", null, 1, 0);

    private static async Task<PipelineReport> Run(IPipelineMergeSummarizer m, bool enabled, Func<FailedJob, JobAnalysis> f, params long[] ids)
    {
        var a = new PipelineAnalyzer(new FakeGitLab(ids.Select(Job).ToArray()), new StageAnalizatorResolver([new FakeStage(f)]),
            new DefaultAnalysisAggregator(), Options.Create(new AnalysisOptions()), m,
            Options.Create(new PipelineMergeOptions { Enabled = enabled }));
        return await a.AnalyzeAsync("1", 1, default);
    }

    [Fact]
    public async Task MergesWhenEnabledAndAtLeastTwoSucceeded()
    {
        var m = new FakeMerge();
        var r = await Run(m, true, j => j.Id == 3 ? new JobAnalysis(j, false, null, "err", 0, 0) : Ok(j), 1, 2, 3);
        Assert.Equal(1, m.Calls);
        Assert.Equal("scalone", r.MergedSummary);
        Assert.Null(r.MergeError);
        Assert.Equal([1L, 2L], m.Input!.Select(a => a.Job.Id));
    }

    [Fact]
    public async Task NotCalledForOneSuccessfulOrWhenDisabled()
    {
        var m = new FakeMerge();
        var r1 = await Run(m, true, j => j.Id == 2 ? new JobAnalysis(j, false, null, "err", 0, 0) : Ok(j), 1, 2);
        var r2 = await Run(m, false, Ok, 1, 2);
        Assert.Equal(0, m.Calls);
        Assert.Null(r1.MergedSummary);
        Assert.Null(r2.MergedSummary);
        Assert.Null(r2.MergeError);
    }

    [Fact]
    public async Task MergeFailureKeepsReportAndSetsError()
    {
        var r = await Run(new FakeMerge(() => throw new LlmException("boom")), true, Ok, 1, 2);
        Assert.Null(r.MergedSummary);
        Assert.Equal("boom", r.MergeError);
        Assert.Equal(2, r.Analyses.Count);
    }

    [Fact]
    public async Task UnexpectedMergeExceptionAlsoDoesNotThrow()
    {
        var r = await Run(new FakeMerge(() => throw new TimeoutException("sk-secret")), true, Ok, 1, 2);
        Assert.NotNull(r.MergeError);
        Assert.DoesNotContain("sk-secret", r.MergeError);
    }

    // --- summarizer / HTTP ---

    private sealed class Stub : HttpMessageHandler
    {
        public List<string?> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Bodies.Add(r.Content is null ? null : await r.Content.ReadAsStringAsync(ct));
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"wspolne\"}}]}", Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class Factory(Stub stub, PipelineMergeOptions o) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal("PipelineMerge", name);
            var c = new HttpClient(stub, false);
            HttpClientHeaders.Apply(c, o);
            return c;
        }
    }

    [Fact]
    public async Task RequestContainsOnlySummaries()
    {
        var o = new PipelineMergeOptions { BaseAddress = "https://m.test/v1/", Model = "mm", SystemPrompt = "SYS", ApiKey = "k" };
        var stub = new Stub();
        var s = new PipelineMergeSummarizer(new Factory(stub, o), Options.Create(o));
        var job = new FailedJob(5, "SECRETNAME", "test", "https://gitlab/SECRETURL", false);
        var res = await s.MergeAsync([new(job, true, "alfa", null, 3, 0), new(job, true, "beta", null, 3, 0)], default);

        Assert.Equal("wspolne", res);
        var body = Assert.Single(stub.Bodies)!;
        var j = System.Text.Json.JsonDocument.Parse(body).RootElement;
        Assert.Equal("mm", j.GetProperty("model").GetString());
        Assert.Equal("Podsumowanie 1:\nalfa\n\nPodsumowanie 2:\nbeta", j.GetProperty("messages")[1].GetProperty("content").GetString());
        Assert.DoesNotContain("SECRETNAME", body);
        Assert.DoesNotContain("SECRETURL", body);
    }

    // --- writer ---

    [Fact]
    public void WriterPrintsMergedSectionBeforeJobsAndError()
    {
        var a = Ok(Job(1));
        var w = new StringWriter();
        ConsoleReportWriter.Write(new PipelineReport(1, 1, 1, 0, [a], "WSPOLNE"), w);
        var t = w.ToString();
        Assert.Contains("── Podsumowanie wspólne ──", t);
        Assert.True(t.IndexOf("WSPOLNE") < t.IndexOf("job1"));
        Assert.DoesNotContain("Scalanie nie powiodło się", t);

        w = new StringWriter();
        ConsoleReportWriter.Write(new PipelineReport(1, 1, 1, 0, [a], null, "boom"), w);
        Assert.Contains("Scalanie nie powiodło się: boom", w.ToString());
        Assert.DoesNotContain("Podsumowanie wspólne", w.ToString());
    }

    // --- config / DI ---

    private static Dictionary<string, string?> Base() => new()
    {
        ["GitLab:BaseAddress"] = "https://g.test/api/v4/", ["GitLab:ApiKey"] = "k", ["GitLab:ProjectId"] = "7",
        ["TestStageAnalysis:BaseAddress"] = "https://l.test/v1/", ["TestStageAnalysis:ApiKey"] = "k", ["TestStageAnalysis:Model"] = "m",
    };

    private static ServiceProvider Di(Dictionary<string, string?> d) =>
        new ServiceCollection().AddPipelineClip(new ConfigurationBuilder().AddInMemoryCollection(d).Build())
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

    [Fact]
    public void Di_DisabledWithoutSection_Builds()
    {
        using var sp = Di(Base());
        Assert.False(sp.GetRequiredService<IOptions<PipelineMergeOptions>>().Value.Enabled);
        Assert.NotNull(sp.GetRequiredService<PipelineAnalyzer>());
        Assert.NotNull(sp.GetRequiredService<IHttpClientFactory>().CreateClient("PipelineMerge"));
    }

    [Fact]
    public void Di_EnabledWithConfig_Builds_AndWithoutConfig_Fails()
    {
        var d = Base();
        d["PipelineMerge:Enabled"] = "true";
        using (var bad = Di(d))
        {
            var ex = Assert.Throws<OptionsValidationException>(() => bad.GetRequiredService<IOptions<PipelineMergeOptions>>().Value);
            Assert.Contains("PipelineMerge", ex.Message);
        }
        d["PipelineMerge:BaseAddress"] = "https://mm.test/v1/";
        d["PipelineMerge:ApiKey"] = "k";
        d["PipelineMerge:Model"] = "mm";
        using var sp = Di(d);
        Assert.Equal("mm", sp.GetRequiredService<IOptions<PipelineMergeOptions>>().Value.Model);
        Assert.Equal("https://mm.test/v1/", sp.GetRequiredService<IHttpClientFactory>().CreateClient("PipelineMerge").BaseAddress!.ToString());
    }

    [Fact]
    public void SectionBinds()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PipelineMerge:Enabled"] = "true", ["PipelineMerge:Model"] = "x", ["PipelineMerge:SystemPrompt"] = "p",
            ["PipelineMerge:MaxTokens"] = "5", ["PipelineMerge:Headers:api-key"] = "{ApiKey}",
        }).Build();
        var o = cfg.GetSection("PipelineMerge").Get<PipelineMergeOptions>()!;
        Assert.True(o.Enabled);
        Assert.Equal("x", o.Model);
        Assert.Equal("p", o.SystemPrompt);
        Assert.Equal(5, o.MaxTokens);
        Assert.Equal("{ApiKey}", o.Headers["api-key"]);
        Assert.False(new PipelineMergeOptions().Enabled);
    }
}
