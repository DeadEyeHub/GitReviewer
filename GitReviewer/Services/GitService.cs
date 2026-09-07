using System.Diagnostics;
using System.Globalization;
using GitReviewer.Models;

namespace GitReviewer.Services;

public sealed class GitService
{
    public async Task ValidateRepositoryAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(repositoryPath))
            throw new DirectoryNotFoundException(Localization.Format(
                "Folder not found: {0}", "Папка не найдена: {0}", repositoryPath));

        var result = await RunAsync(repositoryPath, cancellationToken,
            "rev-parse", "--is-inside-work-tree");
        if (result.ExitCode != 0 || !result.Output.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
            throw new GitException(Localization.Text(
                "The selected folder is not a Git repository.",
                "Выбранная папка не является Git-репозиторием."));
    }

    public async Task<string> GetHeadAsync(string repositoryPath, CancellationToken cancellationToken) =>
        (await RunRequiredAsync(repositoryPath, cancellationToken, "rev-parse", "HEAD")).Trim();

    public async Task<string> ResolveCommitAsync(
        string repositoryPath,
        string revision,
        CancellationToken cancellationToken)
    {
        var value = revision.Trim();
        if (value.Length is < 4 or > 40 || !value.All(Uri.IsHexDigit))
            throw new GitException(Localization.Text(
                "Enter a commit SHA containing 4 to 40 hexadecimal characters.",
                "Введите SHA коммита из 4-40 шестнадцатеричных символов."));
        return (await RunRequiredAsync(repositoryPath, cancellationToken,
            "rev-parse", "--verify", $"{value}^{{commit}}")).Trim();
    }

    public async Task<string> GetBranchAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        var branch = (await RunRequiredAsync(repositoryPath, cancellationToken,
            "branch", "--show-current")).Trim();
        if (branch.Length > 0)
            return branch;
        var head = (await RunRequiredAsync(repositoryPath, cancellationToken,
            "rev-parse", "--short", "HEAD")).Trim();
        return $"detached-{head}";
    }

    public async Task<string> GetRemoteSummaryAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        var result = await RunAsync(repositoryPath, cancellationToken, "remote", "-v");
        if (result.ExitCode != 0)
            return string.Empty;
        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;
    }

    public Task<GitResult> PullAsync(string repositoryPath, CancellationToken cancellationToken) =>
        RunAsync(repositoryPath, cancellationToken, "pull", "--ff-only");

    public async Task<bool> IsAncestorAsync(
        string repositoryPath,
        string ancestor,
        string descendant,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(repositoryPath, cancellationToken,
            "merge-base", "--is-ancestor", ancestor, descendant);
        return result.ExitCode == 0;
    }

    public async Task<IReadOnlyList<string>> GetCommitsAfterAsync(
        string repositoryPath,
        string previousCommit,
        string head,
        CancellationToken cancellationToken)
    {
        var output = await RunRequiredAsync(repositoryPath, cancellationToken,
            "rev-list", "--reverse", "--topo-order", $"{previousCommit}..{head}");
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public async Task<CommitInfo> GetCommitInfoAsync(
        string repositoryPath,
        string sha,
        CancellationToken cancellationToken)
    {
        const string format = "%H%x1f%an%x1f%aI%x1f%s";
        var output = (await RunRequiredAsync(repositoryPath, cancellationToken,
            "show", "-s", $"--format={format}", sha)).Trim();
        var parts = output.Split('\x1f', 4);
        if (parts.Length != 4 || !DateTimeOffset.TryParse(parts[2], CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var date))
            throw new GitException(Localization.Format(
                "Could not read commit data for {0}.",
                "Не удалось прочитать данные коммита {0}.",
                sha));
        return new CommitInfo(parts[0], parts[1], date, parts[3]);
    }

    public async Task<string> GetDiffAsync(
        string repositoryPath,
        string sha,
        CancellationToken cancellationToken)
    {
        var parents = (await RunRequiredAsync(repositoryPath, cancellationToken,
            "rev-list", "--parents", "-n", "1", sha))
            .Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries);

        if (parents.Length > 1)
        {
            return await RunRequiredAsync(repositoryPath, cancellationToken,
                "diff", "--no-ext-diff", "--no-color", "--unified=5", parents[1], sha, "--");
        }

        return await RunRequiredAsync(repositoryPath, cancellationToken,
            "show", "--format=", "--root", "--no-ext-diff", "--no-color", "--unified=5", sha, "--");
    }

    private static async Task<string> RunRequiredAsync(
        string repositoryPath,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        var result = await RunAsync(repositoryPath, cancellationToken, arguments);
        if (result.ExitCode != 0)
            throw new GitException(result.Error.Length > 0
                ? result.Error.Trim()
                : Localization.Text("Git exited with an error.", "Git завершился с ошибкой."));
        return result.Output;
    }

    private static async Task<GitResult> RunAsync(
        string repositoryPath,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = repositoryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new GitException(Localization.Text(
                    "Could not start Git.", "Не удалось запустить Git."));
        }
        catch (Exception exception) when (exception is not GitException)
        {
            throw new GitException(Localization.Text(
                "Git was not found or could not be started.",
                "Git не найден или не может быть запущен."), exception);
        }

        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return new GitResult(process.ExitCode, await outputTask, await errorTask);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(true);
            throw;
        }
    }
}

public sealed record GitResult(int ExitCode, string Output, string Error);

public sealed class GitException : Exception
{
    public GitException(string message) : base(message) { }
    public GitException(string message, Exception innerException) : base(message, innerException) { }
}
