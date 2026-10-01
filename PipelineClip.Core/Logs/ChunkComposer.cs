using PipelineClip.Core.Abstractions;

namespace PipelineClip.Core.Logs;

public static class ChunkComposer
{
    public const string Separator = "[...]";

    public static string Compose(IEnumerable<ScoredChunk> selected)
    {
        var parts = new List<string>();
        var cur = new List<string>();
        var curEnd = 0;
        foreach (var c in selected.Select(s => s.Chunk).OrderBy(c => c.StartLine).ThenBy(c => c.Index))
        {
            var lines = c.Text.Split('\n');
            if (cur.Count > 0 && c.StartLine <= curEnd + 1)
            {
                if (c.EndLine > curEnd)
                {
                    cur.AddRange(lines[(curEnd + 1 - c.StartLine)..]);
                    curEnd = c.EndLine;
                }
                continue;
            }
            if (cur.Count > 0) parts.Add(string.Join('\n', cur));
            cur = [.. lines];
            curEnd = c.EndLine;
        }
        if (cur.Count > 0) parts.Add(string.Join('\n', cur));
        return string.Join("\n" + Separator + "\n", parts);
    }
}
