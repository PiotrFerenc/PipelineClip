namespace PipelineClip.Core.Abstractions;

public interface IGitLabClient
{
    Task<IReadOnlyList<FailedJob>> GetFailedJobsAsync(string projectId, long pipelineId, CancellationToken ct);

    /// <summary>Returns at most the last MaxLogBytes of the job trace.</summary>
    Task<string> GetJobTraceAsync(string projectId, long jobId, CancellationToken ct);
}

public interface IStageAnalizator
{
    string Stage { get; }
    Task<JobAnalysis> AnalyzeAsync(string projectId, FailedJob job, CancellationToken ct);
}

public interface IStageAnalizatorResolver
{
    IStageAnalizator? Resolve(string stage);
}

public interface IRetriever
{
    IReadOnlyList<ScoredChunk> Retrieve(IReadOnlyList<LogChunk> chunks, int top);
}

public interface IReranker
{
    IReadOnlyList<ScoredChunk> Rerank(IReadOnlyList<ScoredChunk> candidates, int top);
}

public interface ITestStageSummarizer
{
    Task<string> SummarizeAsync(string userContent, CancellationToken ct);
}

public interface IPipelineMergeSummarizer
{
    Task<string> MergeAsync(IReadOnlyList<JobAnalysis> successful, CancellationToken ct);
}

public interface ITextAnonymizer
{
    /// <summary>Returns anonymized text and the number of replaced items.</summary>
    (string Text, int Items) Anonymize(string text);
}

public interface IAnalysisAggregator
{
    PipelineReport Aggregate(long pipelineId, int failedTotal, int skipped, IReadOnlyList<JobAnalysis> analyses);
}
