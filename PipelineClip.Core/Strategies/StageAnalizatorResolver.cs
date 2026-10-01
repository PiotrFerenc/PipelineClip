using PipelineClip.Core.Abstractions;

namespace PipelineClip.Core.Strategies;

public sealed class StageAnalizatorResolver : IStageAnalizatorResolver
{
    private readonly Dictionary<string, IStageAnalizator> _byStage;

    public StageAnalizatorResolver(IEnumerable<IStageAnalizator> analizators)
    {
        try
        {
            _byStage = analizators.ToDictionary(a => a.Stage, StringComparer.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException("Dwie strategie zarejestrowane dla tego samego stage'a.");
        }
    }

    public IStageAnalizator? Resolve(string stage) => _byStage.GetValueOrDefault(stage);
}
