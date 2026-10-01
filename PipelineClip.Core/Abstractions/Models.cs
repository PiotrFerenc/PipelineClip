namespace PipelineClip.Core.Abstractions;

public sealed record FailedJob(long Id, string Name, string Stage, string WebUrl, bool AllowFailure);

public sealed record LogChunk(int Index, int StartLine, int EndLine, string Text);

public sealed record ScoredChunk(LogChunk Chunk, double Score);

public sealed record JobAnalysis(
    FailedJob Job,
    bool Succeeded,
    string? Summary,
    string? Error,
    int ChunksSent,
    int AnonymizedItems);

public sealed record PipelineReport(
    long PipelineId,
    int FailedJobsTotal,
    int AnalyzedJobs,
    int SkippedJobs,
    IReadOnlyList<JobAnalysis> Analyses,
    string? MergedSummary = null,
    string? MergeError = null);

public sealed class GitLabException(int? statusCode, string message) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
}

public sealed class LlmException(string message) : Exception(message);
