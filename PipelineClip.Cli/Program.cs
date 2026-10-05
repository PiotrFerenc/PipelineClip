using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PipelineClip.Cli;
using PipelineClip.Core;
using PipelineClip.Core.Orchestration;

var parsed = CliArgs.Parse(args);
if (parsed is null)
{
    Console.Error.WriteLine("Użycie: pipelineclip [pipelineId] [--project <id>] (brakujące wartości z GitLab:PipelineId / GitLab:ProjectId)");
    return 2;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    var builder = Host.CreateApplicationBuilder(args: []);
    builder.Logging.ClearProviders(); // błędy raportuje CLI, logi hosta zawierają dane z pipeline
    builder.Services.AddPipelineClip(builder.Configuration);
    using var host = builder.Build();
    await host.StartAsync(cts.Token); // ValidateOnStart

    var gitlab = host.Services.GetRequiredService<IOptions<GitLabOptions>>().Value;
    var projectId = parsed.Value.ProjectId ?? gitlab.ProjectId;
    var pipelineId = parsed.Value.PipelineId ?? gitlab.PipelineId;
    if (pipelineId <= 0)
    {
        Console.Error.WriteLine("Brak ID pipeline: podaj argument albo ustaw GitLab:PipelineId (liczba > 0).");
        return 2;
    }
    var report = await host.Services.GetRequiredService<PipelineAnalyzer>()
        .AnalyzeAsync(projectId, pipelineId, cts.Token);

    ConsoleReportWriter.Write(report, Console.Out);
    return report.AnalyzedJobs > 0 && report.Analyses.All(a => !a.Succeeded) ? 1 : 0;
}
catch (OperationCanceledException) { return 130; }
catch (Exception ex)
{
    Console.Error.WriteLine($"Błąd: {ex.Message}");
    return 1;
}
