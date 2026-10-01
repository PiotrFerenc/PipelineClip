using Anonymizer.Core;
using Microsoft.Extensions.DependencyInjection;
using PipelineClip.Core.Anonymization;

namespace PipelineClip.Tests.Anonymization;

public class AnonymizationAdapterTests
{
    private static AnonymizationAdapter Make() =>
        new(new ServiceCollection().AddAnonymizer().BuildServiceProvider().GetRequiredService<IAnonymizationService>());

    private const string Log = "error opening '/home/ci/secret/project/file.txt' and \"/home/ci/secret/project/other.txt\"";

    [Fact]
    public void ReplacesPathAndKeepsTokenStable()
    {
        var (text, items) = Make().Anonymize(Log);
        Assert.DoesNotContain("/home/ci/secret/project/", text);
        Assert.Equal(2, items);
        // same directory twice -> same token
        var tokens = System.Text.RegularExpressions.Regex.Matches(text, "'(.*?)file.txt'|\"(.*?)other.txt\"");
        Assert.Equal(2, tokens.Count);
        Assert.Equal(tokens[0].Groups[1].Value, tokens[1].Groups[2].Value);
    }

    [Fact]
    public void SamePathTwiceGetsSameToken()
    {
        var (text, items) = Make().Anonymize("a '/opt/x/y/z.cs' b '/opt/x/y/z.cs'");
        Assert.Equal(2, items);
        var parts = text.Split('\'');
        Assert.Equal(parts[1], parts[3]);
        Assert.NotEqual("/opt/x/y/z.cs", parts[1]);
    }

    [Fact]
    public void ParallelCallsDoNotThrow()
    {
        var a = Make();
        Parallel.For(0, 2000, i =>
        {
            var (t, n) = a.Anonymize($"x '/srv/app{i}/bin/run.dll' y");
            Assert.Equal(1, n);
            Assert.DoesNotContain($"/srv/app{i}/bin/", t);
        });
    }
}
