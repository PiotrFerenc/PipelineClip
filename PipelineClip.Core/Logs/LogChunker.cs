using PipelineClip.Core.Abstractions;

namespace PipelineClip.Core.Logs;

public static class LogChunker
{
    public static IReadOnlyList<LogChunk> Chunk(string clean, int chunkLines, int overlapLines)
    {
        if (chunkLines <= 0) throw new ArgumentOutOfRangeException(nameof(chunkLines));
        if (overlapLines < 0 || overlapLines >= chunkLines)
            throw new ArgumentOutOfRangeException(nameof(overlapLines), "overlap must be in [0, chunkLines)");
        if (clean.Length == 0) return [];

        var lines = clean.Split('\n');
        var step = chunkLines - overlapLines;
        var chunks = new List<LogChunk>();
        for (var start = 0; ; start += step)
        {
            var end = Math.Min(start + chunkLines, lines.Length);
            chunks.Add(new LogChunk(chunks.Count, start + 1, end, string.Join('\n', lines[start..end])));
            if (end == lines.Length) break;
        }
        return chunks;
    }
}
