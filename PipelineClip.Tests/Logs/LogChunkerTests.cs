using PipelineClip.Core.Logs;

namespace PipelineClip.Tests.Logs;

public class LogChunkerTests
{
    private static string Lines(int n) => string.Join('\n', Enumerable.Range(1, n).Select(i => $"l{i}"));

    [Fact]
    public void SlidingWindowsWithOverlap()
    {
        var c = LogChunker.Chunk(Lines(25), 10, 4); // step 6
        Assert.Equal([(1, 10), (7, 16), (13, 22), (19, 25)], c.Select(x => (x.StartLine, x.EndLine)));
        Assert.Equal([0, 1, 2, 3], c.Select(x => x.Index));
        Assert.StartsWith("l7\n", c[1].Text);
        Assert.EndsWith("l25", c[3].Text);
    }

    [Fact]
    public void ExactFitGivesNoTrailingChunk() =>
        Assert.Single(LogChunker.Chunk(Lines(10), 10, 3));

    [Fact]
    public void ShortLogGivesOneChunk()
    {
        var c = Assert.Single(LogChunker.Chunk(Lines(3), 10, 2));
        Assert.Equal((1, 3), (c.StartLine, c.EndLine));
    }

    [Fact]
    public void EmptyLogGivesNoChunks() => Assert.Empty(LogChunker.Chunk("", 10, 2));

    [Theory]
    [InlineData(10, 10)]
    [InlineData(10, 11)]
    public void InvalidOverlapThrows(int size, int overlap) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => LogChunker.Chunk("a", size, overlap));
}
