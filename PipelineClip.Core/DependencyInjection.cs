using Anonymizer.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PipelineClip.Core.Abstractions;
using PipelineClip.Core.Anonymization;
using PipelineClip.Core.GitLab;
using PipelineClip.Core.Llm;
using PipelineClip.Core.Orchestration;
using PipelineClip.Core.Retrieval;
using PipelineClip.Core.Strategies;

namespace PipelineClip.Core;

public static class DependencyInjection
{
    public static IServiceCollection AddPipelineClip(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<GitLabOptions>().Bind(config.GetSection("GitLab"))
            .Validate(o => Http(o) && !string.IsNullOrWhiteSpace(o.ProjectId), "GitLab: wymagane BaseAddress, ApiKey i ProjectId (sekcja GitLab).")
            .ValidateOnStart();
        services.AddOptions<TestStageAnalysisOptions>().Bind(config.GetSection("TestStageAnalysis"))
            .Validate(o => Http(o) && !string.IsNullOrWhiteSpace(o.Model), "TestStageAnalysis: wymagane BaseAddress, ApiKey i Model.")
            .ValidateOnStart();
        services.AddOptions<PipelineMergeOptions>().Bind(config.GetSection("PipelineMerge"))
            .Validate(o => !o.Enabled || (Http(o) && !string.IsNullOrWhiteSpace(o.Model)), "PipelineMerge: wymagane BaseAddress, ApiKey i Model.")
            .ValidateOnStart();
        services.AddOptions<AnalysisOptions>().Bind(config.GetSection("Analysis"))
            .Validate(o => o.ChunkLines > 0 && o.ChunkOverlapLines >= 0 && o.ChunkOverlapLines < o.ChunkLines
                           && o.MaxChunks > 0 && o.RetrieverCandidates > 0 && o.MaxParallelJobs > 0 && o.MaxLogBytes > 0,
                "Analysis: nieprawidłowe limity.")
            .ValidateOnStart();

        services.AddHttpClient("GitLab", (sp, c) => HttpClientHeaders.Apply(c, sp.GetRequiredService<IOptions<GitLabOptions>>().Value));
        services.AddHttpClient("TestStageAnalysis", (sp, c) => HttpClientHeaders.Apply(c, sp.GetRequiredService<IOptions<TestStageAnalysisOptions>>().Value));
        services.AddHttpClient("PipelineMerge", (sp, c) =>
        {
            var o = sp.GetRequiredService<IOptions<PipelineMergeOptions>>().Value;
            if (o.Enabled) HttpClientHeaders.Apply(c, o);
        });

        services.AddAnonymizer();
        services.AddSingleton<IGitLabClient, GitLabClient>();
        services.AddSingleton<IRetriever, Bm25Retriever>();
        services.AddSingleton<IReranker, HeuristicReranker>();
        services.AddSingleton<ITextAnonymizer, AnonymizationAdapter>();
        services.AddSingleton<ITestStageSummarizer, TestStageSummarizer>();
        services.AddSingleton<IPipelineMergeSummarizer, PipelineMergeSummarizer>();
        services.AddSingleton<IStageAnalizator, TestStageAnalizator>();
        services.AddSingleton<IStageAnalizatorResolver, StageAnalizatorResolver>();
        services.AddSingleton<IAnalysisAggregator, DefaultAnalysisAggregator>();
        services.AddSingleton<PipelineAnalyzer>();
        return services;
    }

    private static bool Http(HttpClientOptions o) =>
        Uri.TryCreate(o.BaseAddress, UriKind.Absolute, out _) && !string.IsNullOrWhiteSpace(o.ApiKey);
}
