using PipelineClip.Core.Abstractions;
using PipelineClip.Core.Logs;
using PipelineClip.Core.Retrieval;

namespace PipelineClip.Tests.Retrieval;

public class RetrievalTests
{
    private const string FailedTest = "Total_WithDiscount_ReturnsRoundedValue";

    private static IReadOnlyList<LogChunk> FixtureChunks()
    {
        var raw = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "dotnet-test-failed.log"));
        return LogChunker.Chunk(LogCleaner.Clean(raw), 40, 10);
    }

    private static IReadOnlyList<ScoredChunk> Pipeline(IReadOnlyList<LogChunk> chunks, int candidates = 40, int max = 20)
    {
        var r = new Bm25Retriever().Retrieve(chunks, candidates);
        return new HeuristicReranker().Rerank(r, max);
    }

    [Fact]
    public void FixtureIsRealisticallySized() => Assert.True(File.ReadAllLines(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "dotnet-test-failed.log")).Length >= 300);

    [Fact]
    public void AssertionAndStackChunkSelected_RestoreChunkNot()
    {
        var chunks = FixtureChunks();
        var picked = Pipeline(chunks, max: 3).Select(s => s.Chunk).ToList();
        Assert.Contains(picked, c => c.Text.Contains("Assert.Equal() Failure") && c.Text.Contains("   at Shop.Tests"));
        Assert.DoesNotContain(picked, c => c.Text.Contains("Restore complete"));
    }

    [Fact]
    public void SummaryChunkSelected()
    {
        var picked = Pipeline(FixtureChunks(), max: 3);
        Assert.Contains(picked, s => s.Chunk.Text.Contains("Failed!  - Failed:"));
    }

    [Fact]
    public void SelectedChunksContainFailedTestName()
    {
        var text = ChunkComposer.Compose(Pipeline(FixtureChunks()));
        Assert.Contains(FailedTest, text);
    }

    [Fact]
    public void FallbackReturnsLastChunksWhenNoTerms()
    {
        var log = string.Join('\n', Enumerable.Range(1, 100).Select(i => $"zzz{i} qqq"));
        var chunks = LogChunker.Chunk(log, 10, 0);
        var r = new Bm25Retriever().Retrieve(chunks, 3);
        Assert.Equal([7, 8, 9], r.Select(s => s.Chunk.Index));
    }

    [Fact]
    public void Deterministic()
    {
        var chunks = FixtureChunks();
        Assert.Equal(Pipeline(chunks).Select(s => s.Chunk.Index), Pipeline(chunks).Select(s => s.Chunk.Index));
    }

    [Fact]
    public void TieBreakByIndex()
    {
        var chunks = Enumerable.Range(0, 4).Select(i => new LogChunk(i, 1, 10, "error here")).ToList();
        Assert.Equal([0, 1, 2, 3], new Bm25Retriever().Retrieve(chunks, 10).Select(s => s.Chunk.Index));
    }

    [Fact]
    public void TopLargerThanCountDoesNotThrow()
    {
        var chunks = FixtureChunks();
        Assert.True(new Bm25Retriever().Retrieve(chunks, 1000).Count <= chunks.Count);
        Assert.True(Pipeline(chunks, 1000, 1000).Count <= chunks.Count);
    }

    [Fact]
    public void EmptyInputGivesEmpty()
    {
        Assert.Empty(new Bm25Retriever().Retrieve([], 5));
        Assert.Empty(new HeuristicReranker().Rerank([], 5));
    }
}
