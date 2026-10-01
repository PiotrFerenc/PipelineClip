namespace PipelineClip.Cli;

public static class CliArgs
{
    public static (long PipelineId, string? ProjectId)? Parse(string[] args)
    {
        if (args.Length is not (1 or 3) || !long.TryParse(args[0], out var id) || id <= 0) return null;
        if (args.Length == 1) return (id, null);
        return args[1] == "--project" && args[2].Length > 0 ? (id, args[2]) : null;
    }
}
