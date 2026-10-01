using System.Text.RegularExpressions;

namespace PipelineClip.Core.Logs;

public static partial class LogCleaner
{
    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex Ansi();

    public static string Clean(string raw)
    {
        var text = Ansi().Replace(raw, "").Replace("\r\n", "\n").Replace('\r', '\n');

        var lines = text.Split('\n')
            .Where(l => !l.Contains("section_start:") && !l.Contains("section_end:"))
            .Select(l => l.TrimEnd())
            .ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);

        var result = new List<string>(lines.Count);
        for (var i = 0; i < lines.Count;)
        {
            var j = i;
            while (j < lines.Count && lines[j] == lines[i]) j++;
            var n = j - i;
            if (n >= 3) result.Add($"{lines[i]}  [x {n}]");
            else for (var k = 0; k < n; k++) result.Add(lines[i]);
            i = j;
        }
        return string.Join('\n', result);
    }
}
