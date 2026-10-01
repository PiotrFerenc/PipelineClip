using PipelineClip.Core.Abstractions;

namespace PipelineClip.Core.Orchestration;

public sealed class DefaultAnalysisAggregator : IAnalysisAggregator
{
    public PipelineReport Aggregate(long pipelineId, int failedTotal, int skipped, IReadOnlyList<JobAnalysis> analyses) =>
        new(pipelineId, failedTotal, analyses.Count, skipped, analyses.OrderBy(a => a.Job.Id).ToList());
}
