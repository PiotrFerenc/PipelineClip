using Anonymizer.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PipelineClip.Cli;
using PipelineClip.Core;
using PipelineClip.Core.Abstractions;
using PipelineClip.Core.Orchestration;
using PipelineClip.Core.Retrieval;
using PipelineClip.Core.Strategies;

namespace PipelineClip.Tests.Orchestration;

public class OrchestrationTests
{
    private sealed class FakeGitLab(params FailedJob[] jobs) : IGitLabClient
    {
        public Task<IReadOnlyList<FailedJob>> GetFailedJobsAsync(string p, long id, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<FailedJob>>(jobs);
        public Task<string> GetJobTraceAsync(string p, long jobId, CancellationToken ct) =>
            Task.FromResult($"restore\nerror: Assert.Equal() Failure in /home/ci/builds/proj/Foo.cs\n  at X.Y() in /home/ci/builds/proj/Foo.cs:line 3\nFailed!  - Failed: 1, Passed: 2\n");
    }

    private sealed class FakeSummarizer(Func<string, string>? fn = null) : ITestStageSummarizer
    {
        public List<string> Inputs { get; } = [];
        public int Current, Max;
        public async Task<string> SummarizeAsync(string content, CancellationToken ct)
        {
            lock (Inputs) Inputs.Add(content);
            var c = Interlocked.Increment(ref Current);
            lock (Inputs) Max = Math.Max(Max, c);
            await Task.Delay(30, ct);
            Interlocked.Decrement(ref Current);
            return fn is null ? "ok" : fn(content);
        }
    }

    private sealed class NoMerge : IPipelineMergeSummarizer
    {
        public Task<string> MergeAsync(IReadOnlyList<JobAnalysis> s, CancellationToken ct) => throw new InvalidOperationException();
    }

    private static FailedJob Job(long id, string stage = "test") => new(id, $"job{id}", stage, "", false);

    private static (PipelineAnalyzer, FakeSummarizer) Build(FakeGitLab gl, FakeSummarizer s, int parallel = 4)
    {
        var o = Options.Create(new AnalysisOptions { MaxParallelJobs = parallel });
        var anon = new Core.Anonymization.AnonymizationAdapter(
            new ServiceCollection().AddAnonymizer().BuildServiceProvider().GetRequiredService<IAnonymizationService>());
        var strat = new TestStageAnalizator(gl, new Bm25Retriever(), new HeuristicReranker(), anon, s, o);
        return (new PipelineAnalyzer(gl, new StageAnalizatorResolver([strat]), new DefaultAnalysisAggregator(), o,
            new NoMerge(), Options.Create(new PipelineMergeOptions())), s);
    }

    [Fact]
    public async Task FanOutFanIn_OrdersByJobId_SkipsOtherStages_AnonymizesBeforeLlm()
    {
        var (a, s) = Build(new FakeGitLab(Job(3), Job(1), Job(9, "build")), new FakeSummarizer());
        var r = await a.AnalyzeAsync("1", 100, default);
        Assert.Equal(3, r.FailedJobsTotal);
        Assert.Equal(2, r.AnalyzedJobs);
        Assert.Equal(1, r.SkippedJobs);
        Assert.Equal([1L, 3L], r.Analyses.Select(x => x.Job.Id));
        Assert.All(s.Inputs, i => Assert.DoesNotContain("/home/ci/builds/proj", i));
    }

    [Fact]
    public async Task FailingJob_DoesNotAffectOthers()
    {
        var (a, _) = Build(new FakeGitLab(Job(1), Job(2)),
            new FakeSummarizer(c => c.Contains("job2") ? throw new LlmException("boom") : "ok"));
        var r = await a.AnalyzeAsync("1", 100, default);
        Assert.True(r.Analyses[0].Succeeded);
        Assert.False(r.Analyses[1].Succeeded);
        Assert.Equal("boom", r.Analyses[1].Error);
    }

    [Fact]
    public async Task NoFailedJobs_NoLlmCalls()
    {
        var (a, s) = Build(new FakeGitLab(), new FakeSummarizer());
        var r = await a.AnalyzeAsync("1", 100, default);
        Assert.Equal(0, r.AnalyzedJobs);
        Assert.Empty(s.Inputs);
    }

    [Fact]
    public async Task ParallelismIsCapped()
    {
        var (a, s) = Build(new FakeGitLab(Enumerable.Range(1, 6).Select(i => Job(i)).ToArray()), new FakeSummarizer(), parallel: 2);
        await a.AnalyzeAsync("1", 100, default);
        Assert.Equal(2, s.Max);
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        var (a, _) = Build(new FakeGitLab(Job(1)), new FakeSummarizer());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a.AnalyzeAsync("1", 100, cts.Token));
    }

    [Fact]
    public void Resolver_DuplicateStage_Throws()
    {
        var o = Options.Create(new AnalysisOptions());
        var gl = new FakeGitLab();
        TestStageAnalizator Mk() => new(gl, new Bm25Retriever(), new HeuristicReranker(), null!, null!, o);
        Assert.Throws<InvalidOperationException>(() => new StageAnalizatorResolver([Mk(), Mk()]));
    }

    [Fact]
    public void Di_BuildsAndBindsSections()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GitLab:BaseAddress"] = "https://g.test/api/v4/", ["GitLab:ApiKey"] = "k", ["GitLab:ProjectId"] = "7",
            ["TestStageAnalysis:BaseAddress"] = "https://l.test/v1/", ["TestStageAnalysis:ApiKey"] = "k", ["TestStageAnalysis:Model"] = "m",
        }).Build();
        using var sp = new ServiceCollection().AddPipelineClip(cfg).BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        Assert.NotNull(sp.GetRequiredService<PipelineAnalyzer>());
        Assert.Equal("7", sp.GetRequiredService<IOptions<GitLabOptions>>().Value.ProjectId);
    }

    [Fact]
    public void Di_MissingApiKey_FailsWithSectionName()
    {
        var cfg = new ConfigurationBuilder().Build();
        using var sp = new ServiceCollection().AddPipelineClip(cfg).BuildServiceProvider();
        var ex = Assert.Throws<OptionsValidationException>(() => sp.GetRequiredService<IOptions<GitLabOptions>>().Value);
        Assert.Contains("GitLab", ex.Message);
    }

    [Theory]
    [InlineData("123", 123L, null)]
    [InlineData("123 --project 5", 123L, "5")]
    public void CliArgs_Valid(string line, long id, string? proj)
    {
        var p = CliArgs.Parse(line.Split(' '));
        Assert.Equal((id, proj), p);
    }

    [Theory]
    [InlineData("")] [InlineData("abc")] [InlineData("0")] [InlineData("1 --x 5")] [InlineData("1 --project")]
    public void CliArgs_Invalid(string line) => Assert.Null(CliArgs.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)));

    [Fact]
    public void Report_Formats()
    {
        var w = new StringWriter();
        ConsoleReportWriter.Write(new PipelineReport(1, 0, 0, 0, []), w);
        Assert.Contains("Brak nieudanych jobów test", w.ToString());

        w = new StringWriter();
        ConsoleReportWriter.Write(new PipelineReport(1, 2, 2, 0, [
            new JobAnalysis(Job(1), true, "sum", null, 5, 2), new JobAnalysis(Job(2), false, null, "err", 0, 0)]), w);
        Assert.Contains("sum", w.ToString());
        Assert.Contains("BŁĄD ANALIZY: err", w.ToString());
    }
}
