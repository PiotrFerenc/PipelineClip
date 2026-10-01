using System.Text.RegularExpressions;
using PipelineClip.Core.Abstractions;

namespace PipelineClip.Core.Retrieval;

public sealed partial class Bm25Retriever : IRetriever
{
    private const double K1 = 1.2, B = 0.75;

    private static readonly string[] Query =
    [
        "error", "failed", "failure", "exception", "assert", "assertion", "expected", "actual",
        "fail", "fatal", "panic", "traceback", "timeout", "stack", "at", "exit",
    ];

    [GeneratedRegex(@"[^\p{L}\p{Nd}_]+")]
    private static partial Regex Splitter();

    private static string[] Tokenize(string text) =>
        Splitter().Split(text.ToLowerInvariant()).Where(t => t.Length >= 2).ToArray();

    public IReadOnlyList<ScoredChunk> Retrieve(IReadOnlyList<LogChunk> chunks, int top)
    {
        if (chunks.Count == 0 || top <= 0) return [];

        var tokens = chunks.Select(c => Tokenize(c.Text)).ToArray();
        var avgLen = Math.Max(1.0, tokens.Average(t => t.Length));
        var n = chunks.Count;
        var df = Query.ToDictionary(q => q, q => tokens.Count(t => t.Contains(q)));

        var scored = new List<ScoredChunk>();
        for (var i = 0; i < n; i++)
        {
            var len = tokens[i].Length;
            var tf = tokens[i].Where(t => df.ContainsKey(t)).GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
            var score = 0.0;
            foreach (var (term, f) in tf)
            {
                var idf = Math.Log(1 + (n - df[term] + 0.5) / (df[term] + 0.5));
                score += idf * f * (K1 + 1) / (f + K1 * (1 - B + B * len / avgLen));
            }
            if (score > 0) scored.Add(new ScoredChunk(chunks[i], score));
        }

        if (scored.Count == 0)
            return chunks.OrderByDescending(c => c.Index).Take(top).OrderBy(c => c.Index)
                .Select(c => new ScoredChunk(c, 0)).ToList();

        return scored.OrderByDescending(s => s.Score).ThenBy(s => s.Chunk.Index).Take(top).ToList();
    }
}
