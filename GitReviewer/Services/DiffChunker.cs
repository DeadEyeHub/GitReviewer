using System.Text;
using System.Text.RegularExpressions;

namespace GitReviewer.Services;

public static partial class DiffChunker
{
    private const int MaximumSingleDiffLength = 32_000;
    private const int MaximumChunkContentLength = 31_000;
    private const int OverlapLineCount = 3;

    public static IReadOnlyList<string> Split(string diff)
    {
        if (diff.Length <= MaximumSingleDiffLength)
            return [diff];

        var lines = diff.Replace("\r\n", "\n").Split('\n');
        var sections = new List<List<string>>();
        foreach (var line in lines)
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal) || sections.Count == 0)
                sections.Add([]);
            sections[^1].Add(line);
        }

        var pieces = sections.SelectMany(SplitSection).ToList();
        var chunks = new List<string>();
        var current = new StringBuilder();
        foreach (var piece in pieces)
        {
            if (current.Length > 0 && current.Length + piece.Length > MaximumChunkContentLength)
            {
                chunks.Add(current.ToString());
                current.Clear();
            }

            current.Append(piece);
        }

        if (current.Length > 0)
            chunks.Add(current.ToString());
        return chunks;
    }

    private static IEnumerable<string> SplitSection(List<string> section)
    {
        var firstHunk = section.FindIndex(line => line.StartsWith("@@ ", StringComparison.Ordinal));
        if (firstHunk < 0)
            return SplitRawText(string.Join('\n', section) + '\n');

        var fileHeader = string.Join('\n', section.Take(firstHunk)) + '\n';
        var hunkStarts = section
            .Select((line, index) => (line, index))
            .Where(item => item.index >= firstHunk && item.line.StartsWith("@@ ", StringComparison.Ordinal))
            .Select(item => item.index)
            .ToList();

        var result = new List<string>();
        for (var index = 0; index < hunkStarts.Count; index++)
        {
            var end = index + 1 < hunkStarts.Count ? hunkStarts[index + 1] : section.Count;
            result.AddRange(SplitHunk(fileHeader, section.GetRange(hunkStarts[index], end - hunkStarts[index])));
        }
        return result;
    }

    private static IEnumerable<string> SplitHunk(string fileHeader, List<string> hunk)
    {
        var completeHunk = fileHeader + string.Join('\n', hunk) + '\n';
        if (completeHunk.Length <= MaximumChunkContentLength)
            return [completeHunk];

        var match = HunkHeaderRegex().Match(hunk[0]);
        if (!match.Success || fileHeader.Length > MaximumChunkContentLength / 2)
            return SplitRawText(completeHunk);

        var oldLine = int.Parse(match.Groups["old"].Value);
        var newLine = int.Parse(match.Groups["new"].Value);
        var suffix = match.Groups["suffix"].Value;
        if (suffix.Length > 300)
            suffix = suffix[..300] + "...";
        var diffLines = new List<NumberedDiffLine>();
        foreach (var text in hunk.Skip(1))
        {
            var oldDelta = text.StartsWith(' ') || text.StartsWith('-') ? 1 : 0;
            var newDelta = text.StartsWith(' ') || text.StartsWith('+') ? 1 : 0;
            diffLines.Add(new NumberedDiffLine(text, oldLine, newLine, oldDelta, newDelta));
            oldLine += oldDelta;
            newLine += newDelta;
        }

        var availableLength = Math.Max(
            1000,
            MaximumChunkContentLength - fileHeader.Length - suffix.Length - 100);
        var parts = new List<List<NumberedDiffLine>>();
        var current = new List<NumberedDiffLine>();
        var currentLength = 0;
        foreach (var line in ExpandLongLines(diffLines, availableLength))
        {
            if (current.Count > 0 && currentLength + line.Text.Length + 1 > availableLength)
            {
                parts.Add(current);
                var overlap = current.TakeLast(OverlapLineCount).ToList();
                var overlapLength = overlap.Sum(item => item.Text.Length + 1);
                if (overlapLength + line.Text.Length + 1 <= availableLength)
                {
                    current = overlap;
                    currentLength = overlapLength;
                }
                else
                {
                    current = [];
                    currentLength = 0;
                }
            }

            current.Add(line);
            currentLength += line.Text.Length + 1;
        }

        if (current.Count > 0)
            parts.Add(current);
        return parts.Select(part => BuildHunkPart(fileHeader, suffix, part));
    }

    private static IEnumerable<NumberedDiffLine> ExpandLongLines(
        IEnumerable<NumberedDiffLine> lines,
        int maximumLength)
    {
        foreach (var line in lines)
        {
            if (line.Text.Length <= maximumLength)
            {
                yield return line;
                continue;
            }

            var prefix = line.Text.Length > 0 ? line.Text[0].ToString() : " ";
            var content = line.Text.Length > 0 ? line.Text[1..] : string.Empty;
            var fragmentLength = maximumLength - prefix.Length - 40;
            var fragmentCount = (int)Math.Ceiling((double)content.Length / fragmentLength);
            for (var index = 0; index < fragmentCount; index++)
            {
                var start = index * fragmentLength;
                var length = Math.Min(fragmentLength, content.Length - start);
                var label = $" [long line fragment {index + 1}/{fragmentCount}] ";
                yield return line with
                {
                    Text = prefix + label + content.Substring(start, length),
                    OldDelta = index + 1 == fragmentCount ? line.OldDelta : 0,
                    NewDelta = index + 1 == fragmentCount ? line.NewDelta : 0
                };
            }
        }
    }

    private static string BuildHunkPart(
        string fileHeader,
        string suffix,
        IReadOnlyCollection<NumberedDiffLine> lines)
    {
        var first = lines.First();
        var oldCount = lines.Sum(line => line.OldDelta);
        var newCount = lines.Sum(line => line.NewDelta);
        return $"{fileHeader}@@ -{first.OldLine},{oldCount} +{first.NewLine},{newCount} @@{suffix}\n" +
               string.Join('\n', lines.Select(line => line.Text)) + "\n";
    }

    private static IEnumerable<string> SplitRawText(string text)
    {
        for (var start = 0; start < text.Length; start += MaximumChunkContentLength)
            yield return text.Substring(start, Math.Min(MaximumChunkContentLength, text.Length - start));
    }

    [GeneratedRegex(@"^@@ -(?<old>\d+)(?:,\d+)? \+(?<new>\d+)(?:,\d+)? @@(?<suffix>.*)$")]
    private static partial Regex HunkHeaderRegex();

    private sealed record NumberedDiffLine(
        string Text,
        int OldLine,
        int NewLine,
        int OldDelta,
        int NewDelta);
}
