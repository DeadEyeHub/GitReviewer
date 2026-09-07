using GitReviewer.Models;

namespace GitReviewer.Services;

public static class ReviewParser
{
    public static ReviewResult Parse(string response)
    {
        var result = new ReviewResult();
        var normalized = response.Replace("\r\n", "\n").Trim();
        if (normalized.Equals("NO_BUGS", StringComparison.OrdinalIgnoreCase))
            return result;

        FindingBuilder? current = null;
        var foundBlock = false;
        var hasOutsideContent = false;
        foreach (var rawLine in normalized.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Equals("BUG", StringComparison.OrdinalIgnoreCase))
            {
                AddCurrent(result, current);
                current = new FindingBuilder();
                foundBlock = true;
                continue;
            }

            if (line.Equals("END", StringComparison.OrdinalIgnoreCase))
            {
                AddCurrent(result, current);
                current = null;
                continue;
            }

            if (current is null)
            {
                if (line.Length > 0 && line is not "```" and not "```text")
                    hasOutsideContent = true;
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator < 1)
            {
                if (line.Length > 0)
                    current.Description = current.Description.Length == 0
                        ? line
                        : current.Description + " " + line;
                continue;
            }

            var key = line[..separator].Trim().ToUpperInvariant();
            var value = line[(separator + 1)..].Trim();
            switch (key)
            {
                case "FILE": current.File = value; break;
                case "LINE": current.Line = ParseLine(value); break;
                case "SIDE": current.Side = value.ToUpperInvariant(); break;
                case "DESCRIPTION": current.Description = value; break;
                default:
                    current.Description = current.Description.Length == 0
                        ? line
                        : current.Description + " " + line;
                    break;
            }
        }

        AddCurrent(result, current);
        var declaredBugCount = normalized.Split('\n').Count(line =>
            line.Trim().Equals("BUG", StringComparison.OrdinalIgnoreCase));
        if ((!foundBlock || result.Findings.Count < declaredBugCount || hasOutsideContent) && normalized.Length > 0)
            result.UnstructuredResponse = normalized;
        return result;
    }

    private static int? ParseLine(string value)
    {
        var digits = new string(value.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var line) && line > 0 ? line : null;
    }

    private static void AddCurrent(ReviewResult result, FindingBuilder? current)
    {
        if (current is null)
            return;
        if (current.Description.Length == 0)
            return;

        result.Findings.Add(new Finding(
            current.File.Length == 0
                ? Localization.Text("unknown file", "неизвестный файл")
                : current.File,
            current.Line,
            current.Side.Length == 0 ? "HUNK" : current.Side,
            current.Description));
    }

    private sealed class FindingBuilder
    {
        public string File { get; set; } = string.Empty;
        public int? Line { get; set; }
        public string Side { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }
}
