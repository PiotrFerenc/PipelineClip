using Anonymizer.Core;
using PipelineClip.Core.Abstractions;

namespace PipelineClip.Core.Anonymization;

// AnonymizationService is thread-safe: per-call ReplacementMap, stateless anonymizers, Random.Shared. No lock needed.
public sealed class AnonymizationAdapter(IAnonymizationService service) : ITextAnonymizer
{
    public (string Text, int Items) Anonymize(string text)
    {
        var r = service.Anonymize(text);
        return (r.AnonymizedDocument, r.Anonymized.Count);
    }
}
