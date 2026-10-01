using PipelineClip.Core.Abstractions;
using PipelineClip.Core.Logs;

namespace PipelineClip.Tests.Logs;

public class ChunkComposerTests
{
    private static readonly string[] All = Enumerable.Range(1, 40).Select(i => $"l{i}").ToArray();

    private static ScoredChunk Sc(int idx, int s, int e) =>
        new(new LogChunk(idx, s, e, string.Join('\n', All[(s - 1)..e])), 1);

    [Fact]
    public void MergesOverlappingWithoutDuplicates() =>
        Assert.Equal(string.Join('\n', All[..16]), ChunkComposer.Compose([Sc(1, 7, 16), Sc(0, 1, 10)]));

    [Fact]
    public void MergesAdjacent() =>
        Assert.Equal(string.Join('\n', All[..10]), ChunkComposer.Compose([Sc(0, 1, 5), Sc(1, 6, 10)]));

    [Fact]
    public void SeparatorBetweenDisjoint() =>
        Assert.Equal("l1\nl2\n[...]\nl5\nl6", ChunkComposer.Compose([Sc(1, 5, 6), Sc(0, 1, 2)]));

    [Fact]
    public void ContainedChunkIgnored() =>
        Assert.Equal(string.Join('\n', All[..10]), ChunkComposer.Compose([Sc(0, 1, 10), Sc(1, 3, 5)]));

    [Fact]
    public void EmptyGivesEmpty() => Assert.Equal("", ChunkComposer.Compose([]));
}
