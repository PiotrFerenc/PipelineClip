using System.Text.RegularExpressions;
using PipelineClip.Core.Abstractions;

namespace PipelineClip.Core.Retrieval;

public sealed partial class HeuristicReranker : IReranker
{
    // ponytail: wagi i wzorce pod dotnet/xunit; sufit: inne frameworki (pytest, jest, go test) nie dostaja bonusow,
    // wtedy liczy sie tylko BM25 + bliskosc konca. Upgrade: dopisac wzorce lub wagi z konfiguracji.
    private const double SummaryBonus = 1.5;
    private const double AssertBonus = 1.0;
    private const double ExceptionBonus = 1.0;
    private const double StackBonus = 0.7;
    private const double ExitBonus = 0.5;
    private const double ProximityMax = 0.8;
    private const double NoisePenalty = -1.0;

    [GeneratedRegex(@"Failed!\s+-\s+Failed:\s+\d+|Failed\s+\S+\s+\[\d+\s*m?s\]")]
    private static partial Regex Summary();
    [GeneratedRegex(@"Assert\.|Expected:|Actual:|Assert\.Equal\(\) Failure")]
    private static partial Regex Assertion();
    [GeneratedRegex(@"\b\w+Exception\b")]
    private static partial Regex Exception();
    [GeneratedRegex(@"^\s+at\s+", RegexOptions.Multiline)]
    private static partial Regex Stack();
    [GeneratedRegex(@"exit code|ERROR: Job failed|fatal:", RegexOptions.IgnoreCase)]
    private static partial Regex Exit();
    [GeneratedRegex(@"Restore(d)? complete|Pulling docker image|Downloading|Using docker image")]
    private static partial Regex Noise();

    public IReadOnlyList<ScoredChunk> Rerank(IReadOnlyList<ScoredChunk> candidates, int top)
    {
        if (candidates.Count == 0 || top <= 0) return [];

        var maxScore = candidates.Max(c => c.Score);
        var maxEnd = Math.Max(1, candidates.Max(c => c.Chunk.EndLine));

        return candidates
            .Select(c =>
            {
                var t = c.Chunk.Text;
                var s = maxScore > 0 ? c.Score / maxScore : 0;
                if (Summary().IsMatch(t)) s += SummaryBonus;
                if (Assertion().IsMatch(t)) s += AssertBonus;
                if (Exception().IsMatch(t)) s += ExceptionBonus;
                if (Stack().IsMatch(t)) s += StackBonus;
                if (Exit().IsMatch(t)) s += ExitBonus;
                if (Noise().IsMatch(t)) s += NoisePenalty;
                s += ProximityMax * c.Chunk.EndLine / maxEnd;
                return new ScoredChunk(c.Chunk, s);
            })
            .OrderByDescending(s => s.Score).ThenBy(s => s.Chunk.Index)
            .Take(top).ToList();
    }
}
