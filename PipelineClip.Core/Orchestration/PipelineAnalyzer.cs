using Microsoft.Extensions.Options;
using PipelineClip.Core.Abstractions;

namespace PipelineClip.Core.Orchestration;

public sealed class PipelineAnalyzer(
    IGitLabClient gitLab,
    IStageAnalizatorResolver resolver,
    IAnalysisAggregator aggregator,
    IOptions<AnalysisOptions> options,
    IPipelineMergeSummarizer merger,
    IOptions<PipelineMergeOptions> mergeOptions)
{
    public async Task<PipelineReport> AnalyzeAsync(string projectId, long pipelineId, CancellationToken ct)
    {
        var jobs = await gitLab.GetFailedJobsAsync(projectId, pipelineId, ct);
        var work = jobs
            .Select(j => (Job: j, Strategy: resolver.Resolve(j.Stage)))
            .ToList();
        var analyzable = work.Where(w => w.Strategy is not null).ToList();

        using var gate = new SemaphoreSlim(options.Value.MaxParallelJobs);
        var analyses = await Task.WhenAll(analyzable.Select(async w =>
        {
            await gate.WaitAsync(ct);
            try { return await w.Strategy!.AnalyzeAsync(projectId, w.Job, ct); }
            finally { gate.Release(); }
        }));

        var report = aggregator.Aggregate(pipelineId, jobs.Count, work.Count - analyzable.Count, analyses);

        var ok = report.Analyses.Where(a => a.Succeeded).ToList();
        if (!mergeOptions.Value.Enabled || ok.Count < 2) return report;
        try
        {
            return report with { MergedSummary = await merger.MergeAsync(ok, ct) };
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // komunikat tylko z LlmException (bez klucza); inne wyjątki: sam typ
            return report with { MergeError = e is LlmException ? e.Message : $"Błąd scalania: {e.GetType().Name}" };
        }
    }
}
