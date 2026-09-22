using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using GitReviewer.Models;

namespace GitReviewer.Services;

public sealed class ReportWriter
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public string GetReportPath(string repositoryPath, string branch, string? repositoryIdentity = null)
    {
        var identityPath = Path.GetFullPath(repositoryIdentity ?? repositoryPath);
        var identityDirectory = new DirectoryInfo(identityPath);
        var repositoryName = identityDirectory.Name.Equals(".git", StringComparison.OrdinalIgnoreCase) &&
                             identityDirectory.Parent is not null
            ? identityDirectory.Parent.Name
            : new DirectoryInfo(repositoryPath).Name;
        var identity = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{identityPath}|{branch}")))[..10];
        var fileName = SanitizeFileName($"{repositoryName}-{branch}-{identity}-review.md");
        return Path.Combine(AppPaths.ReportsDirectory, fileName);
    }

    public async Task AppendAsync(
        string repositoryPath,
        string branch,
        CommitInfo commit,
        ReviewResult result,
        bool manualReview,
        CancellationToken cancellationToken,
        string? repositoryIdentity = null)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var path = GetReportPath(repositoryPath, branch, repositoryIdentity);
            var legacyPath = repositoryIdentity is null
                ? path
                : GetReportPath(repositoryPath, branch);
            var sourcePath = !File.Exists(path) && File.Exists(legacyPath)
                ? legacyPath
                : path;
            var marker = manualReview
                ? $"<!-- manual-review:{commit.Sha}:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()} -->"
                : $"<!-- commit:{commit.Sha} -->";
            var existing = string.Empty;
            if (File.Exists(sourcePath))
            {
                existing = await File.ReadAllTextAsync(sourcePath, cancellationToken);
            }

            var text = new StringBuilder();
            if (!File.Exists(sourcePath))
                text.AppendLine(Localization.Text("# Git review report", "# Отчет проверки Git")).AppendLine();

            text.AppendLine(marker)
                .AppendLine($"## `{commit.Sha[..Math.Min(8, commit.Sha.Length)]}` - {Escape(commit.Subject)}{ManualLabel(manualReview)}")
                .AppendLine()
                .AppendLine(Localization.Format("- Branch: {0}", "- Ветка: {0}", Escape(branch)))
                .AppendLine(Localization.Format("- Author: {0}", "- Автор: {0}", Escape(commit.Author)))
                .AppendLine(Localization.Format(
                    "- Date: {0:yyyy-MM-dd HH:mm:ss zzz}",
                    "- Дата: {0:yyyy-MM-dd HH:mm:ss zzz}",
                    commit.Date))
                .AppendLine(Localization.Format("- Result: {0}", "- Результат: {0}", DescribeResult(result)))
                .AppendLine();

            foreach (var finding in result.Findings)
            {
                var line = finding.Line?.ToString() ?? Localization.Text("line unknown", "строка неизвестна");
                var side = finding.Side.Equals("OLD", StringComparison.OrdinalIgnoreCase)
                    ? Localization.Text(", deleted line", ", удаленная строка")
                    : finding.Side.Equals("HUNK", StringComparison.OrdinalIgnoreCase)
                        ? Localization.Text(", changed block", ", измененный блок")
                        : string.Empty;
                text.AppendLine($"### `{Escape(finding.File)}:{line}`{side}")
                    .AppendLine()
                    .AppendLine(Escape(finding.Description))
                    .AppendLine();
            }

            if (result.UnstructuredResponse is not null)
            {
                text.AppendLine(Localization.Text(
                        "### Unstructured model response",
                        "### Неструктурированный ответ модели"))
                    .AppendLine()
                    .AppendLine(Localization.Text(
                        "The response could not be fully parsed and was preserved:",
                        "Ответ не удалось полностью разобрать, поэтому он сохранен целиком:"))
                    .AppendLine()
                    .AppendLine("```text")
                    .AppendLine(result.UnstructuredResponse.Replace("```", "'''"))
                    .AppendLine("```")
                    .AppendLine();
            }

            var temporaryPath = path + ".tmp";
            var start = existing.Length;
            var end = existing.Length;
            if (!manualReview)
            {
                // A cursor-save retry may produce a different model result. Replace its
                // automatic entry, preserving adjacent entries and markers inside code fences.
                var offset = 0;
                var inFence = false;
                var lines = existing.Split('\n');
                for (var index = 0; index < lines.Length; index++)
                {
                    var rawLine = lines[index];
                    var line = rawLine.TrimEnd('\r');
                    if (line.StartsWith("```", StringComparison.Ordinal)) inFence = !inFence;
                    if (!inFence && index + 1 < lines.Length &&
                        lines[index + 1].StartsWith("## `", StringComparison.Ordinal))
                    {
                        if (start == existing.Length && line == marker) start = offset;
                        else if (start != existing.Length &&
                                 (line.StartsWith("<!-- commit:", StringComparison.Ordinal) ||
                                  line.StartsWith("<!-- manual-review:", StringComparison.Ordinal)))
                        {
                            end = offset;
                            break;
                        }
                    }
                    offset += rawLine.Length + 1;
                }
            }
            await File.WriteAllTextAsync(temporaryPath,
                existing[..start] + text + existing[end..], Encoding.UTF8, cancellationToken);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Open(string repositoryPath, string branch, string? repositoryIdentity = null)
    {
        var path = GetReportPath(repositoryPath, branch, repositoryIdentity);
        if (!File.Exists(path) && repositoryIdentity is not null)
            path = GetReportPath(repositoryPath, branch);
        if (!File.Exists(path))
            throw new FileNotFoundException(Localization.Text(
                "No report exists for the selected project yet.",
                "Для выбранного проекта пока нет отчета."), path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static string DescribeResult(ReviewResult result)
    {
        if (result.EmptyDiff)
            return Localization.Text("empty diff; no model request", "пустой diff; запрос к модели не выполнялся");
        if (result.Findings.Count > 0)
            return Localization.Format(
                "potential bugs: {0}",
                "потенциальных ошибок: {0}",
                result.Findings.Count);
        return result.UnstructuredResponse is null
            ? Localization.Text("no bugs found", "ошибок не найдено")
            : Localization.Text("unstructured model response", "неструктурированный ответ модели");
    }

    private static string ManualLabel(bool manualReview) => manualReview
        ? Localization.Text(" (manual review)", " (ручная проверка)")
        : string.Empty;

    private static string Escape(string value) => value.Replace("\r", " ").Replace("\n", " ");

    private static string SanitizeFileName(string fileName)
    {
        foreach (var character in Path.GetInvalidFileNameChars())
            fileName = fileName.Replace(character, '_');
        return fileName;
    }
}
