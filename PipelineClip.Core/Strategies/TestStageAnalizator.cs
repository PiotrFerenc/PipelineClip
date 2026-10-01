using Microsoft.Extensions.Options;
using PipelineClip.Core.Abstractions;
using PipelineClip.Core.Logs;

namespace PipelineClip.Core.Strategies;

public sealed class TestStageAnalizator(
    IGitLabClient gitLab,
    IRetriever retriever,
    IReranker reranker,
    ITextAnonymizer anonymizer,
    ITestStageSummarizer summarizer,
    IOptions<AnalysisOptions> options) : IStageAnalizator
{
    private readonly AnalysisOptions _o = options.Value;

    public string Stage => _o.TargetStage;

    public async Task<JobAnalysis> AnalyzeAsync(string projectId, FailedJob job, CancellationToken ct)
    {
        try
        {
            var clean = LogCleaner.Clean(await gitLab.GetJobTraceAsync(projectId, job.Id, ct));
            if (string.IsNullOrWhiteSpace(clean))
                return new JobAnalysis(job, false, null, "Pusty log joba", 0, 0);

            var chunks = LogChunker.Chunk(clean, _o.ChunkLines, _o.ChunkOverlapLines);
            var best = reranker.Rerank(retriever.Retrieve(chunks, _o.RetrieverCandidates), _o.MaxChunks);
            var (text, items) = anonymizer.Anonymize(ChunkComposer.Compose(best));
            var summary = await summarizer.SummarizeAsync($"Job: {job.Name} (stage: {job.Stage})\n\n{text}", ct);
            return new JobAnalysis(job, true, summary, null, best.Count, items);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new JobAnalysis(job, false, null, ex.Message, 0, 0);
        }
    }
}
