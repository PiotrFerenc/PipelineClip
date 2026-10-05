namespace PipelineClip.Cli;

public static class CliArgs
{
    // [pipelineId] [--project <id>]; brakujące wartości przychodzą z configu (GitLab:PipelineId, GitLab:ProjectId)
    public static (long? PipelineId, string? ProjectId)? Parse(string[] args)
    {
        long? pipeline = null;
        string? project = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--project" && project is null && i + 1 < args.Length && args[i + 1].Length > 0) project = args[++i];
            else if (pipeline is null && long.TryParse(args[i], out var id) && id > 0) pipeline = id;
            else return null;
        }
        return (pipeline, project);
    }
}
