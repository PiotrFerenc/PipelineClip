using PipelineClip.Core.Logs;

namespace PipelineClip.Tests.Logs;

public class LogCleanerTests
{
    [Fact]
    public void RemovesAnsi() =>
        Assert.Equal("red ok", LogCleaner.Clean("\x1B[31;1mred\x1B[0m ok"));

    [Fact]
    public void NormalizesCrAndCrLf() =>
        Assert.Equal("a\nb\nc", LogCleaner.Clean("a\r\nb\rc"));

    [Fact]
    public void RemovesSectionMarkers() =>
        Assert.Equal("x\ny", LogCleaner.Clean("section_start:1234:build\nx\n\x1B[0Ksection_end:1234:build\ny"));

    [Fact]
    public void CollapsesThreeOrMoreRepeats() =>
        Assert.Equal("a\nsame  [x 4]\nb\nz\nz\nc", LogCleaner.Clean("a\nsame\nsame\nsame\nsame\nb\nz\nz\nc"));

    [Fact]
    public void TrimsLineEnds() =>
        Assert.Equal("a\n b", LogCleaner.Clean("a   \n b\t"));
}
