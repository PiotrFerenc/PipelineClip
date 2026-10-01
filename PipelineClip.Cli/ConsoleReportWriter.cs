using PipelineClip.Core.Abstractions;

namespace PipelineClip.Cli;

public static class ConsoleReportWriter
{
    public static void Write(PipelineReport r, TextWriter w)
    {
        if (r.AnalyzedJobs == 0)
        {
            w.WriteLine($"Pipeline #{r.PipelineId}: Brak nieudanych jobów test.");
            return;
        }
        w.WriteLine($"Pipeline #{r.PipelineId}: {r.FailedJobsTotal} nieudane joby, {r.AnalyzedJobs} przeanalizowane, {r.SkippedJobs} pominięty (stage bez strategii)");
        if (r.MergedSummary != null)
        {
            w.WriteLine();
            w.WriteLine("── Podsumowanie wspólne ──");
            w.WriteLine(r.MergedSummary);
        }
        if (r.MergeError != null)
        {
            w.WriteLine();
            w.WriteLine($"Scalanie nie powiodło się: {r.MergeError}");
        }
        foreach (var a in r.Analyses)
        {
            w.WriteLine();
            w.WriteLine($"── {a.Job.Name} (job {a.Job.Id}) ───────────────");
            if (a.Succeeded)
            {
                w.WriteLine(a.Summary);
                w.WriteLine($"(fragmentów: {a.ChunksSent}, zanonimizowanych elementów: {a.AnonymizedItems})");
            }
            else w.WriteLine($"BŁĄD ANALIZY: {a.Error}");
        }
    }
}
