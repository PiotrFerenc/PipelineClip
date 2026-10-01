using Anonymizer.Core;
using Microsoft.Extensions.DependencyInjection;
using PipelineClip.Core.Anonymization;

namespace PipelineClip.Tests.Anonymization;

public class SecretRedactionTests
{
    [Fact]
    public void RemovesSecretsAndEmail_StillAnonymizesPath()
    {
        var adapter = new AnonymizationAdapter(
            new ServiceCollection().AddAnonymizer().BuildServiceProvider().GetRequiredService<IAnonymizationService>());
        const string log =
            "curl -H 'Authorization: Bearer eyJhbGciOi.abc' https://ci.example/api?token=s3cr3tvalue\n" +
            "git clone https://oauth2:glpat-abcdefghij1234567890@gitlab.example/g/p.git\n" +
            "Server=db;Password=Sup3rPass;Port=1; contact dev@firma.pl from 10.20.30.40\n" +
            "FAILED at '/home/ci/builds/project/Foo.cs' line 3, version 1.2.3";

        var (text, items) = adapter.Anonymize(log);

        foreach (var s in new[] { "eyJhbGciOi.abc", "s3cr3tvalue", "glpat-", "Sup3rPass", "dev@firma.pl", "10.20.30.40", "/home/ci/builds/project/" })
            Assert.DoesNotContain(s, text);
        Assert.Contains("Authorization: Bearer ", text);
        Assert.Contains("Password=", text);
        Assert.Contains("/Foo.cs", text);
        Assert.Contains("version 1.2.3", text);
        Assert.True(items >= 7);
    }
}
