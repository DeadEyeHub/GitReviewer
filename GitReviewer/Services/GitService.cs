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

    public async Task<GitResult> PullAsync(
        string repositoryPath,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var remote = await TryGetUpstreamRemoteAsync(repositoryPath, cancellationToken);
        if (remote is not null && remote.Value.Url != ".")
            ValidateRemoteForAuthenticationMode(remote.Value.Url, settings.GitAuthenticationMode);
        return await RunWithAuthenticationAsync(
            repositoryPath, settings, cancellationToken, "pull", "--ff-only");
    }

    public async Task TestRemoteAccessAsync(
        string repositoryPath,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        await ValidateRepositoryAsync(repositoryPath, cancellationToken);
        var remote = await TryGetUpstreamRemoteAsync(repositoryPath, cancellationToken)
            ?? throw new GitException(Localization.Text(
                "The current branch does not track a remote branch.",
                "Текущая ветка не отслеживает удаленную ветку."));
        if (remote.Url != ".")
            ValidateRemoteForAuthenticationMode(remote.Url, settings.GitAuthenticationMode);

        var result = await RunWithAuthenticationAsync(repositoryPath, settings, cancellationToken,
            "ls-remote", "--exit-code", remote.Name, remote.MergeReference);
        if (result.ExitCode != 0)
            throw new GitException(result.Error.Length > 0
                ? result.Error.Trim()
                : Localization.Text(
                    "Could not access the remote repository.",
                    "Не удалось получить доступ к удаленному репозиторию."));
    }

    public async Task<string> DetectAuthenticationModeAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        var remote = await TryGetUpstreamRemoteAsync(repositoryPath, cancellationToken);
        if (remote is null)
        {
            var origin = await RunAsync(repositoryPath, cancellationToken,
                "remote", "get-url", "origin");
            if (origin.ExitCode != 0)
                return "ssh-agent";
            return origin.Output.Trim().StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? "https"
                : "ssh-agent";
        }

        return remote.Value.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? "https"
            : "ssh-agent";
    }

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
        => await RunCoreAsync(repositoryPath, null, cancellationToken, arguments);

    private static async Task<GitResult> RunWithAuthenticationAsync(
        string repositoryPath,
        AppSettings settings,
        CancellationToken cancellationToken,
        params string[] arguments)
        => await RunCoreAsync(repositoryPath, settings, cancellationToken, arguments);

    private static async Task<GitResult> RunCoreAsync(
        string repositoryPath,
        AppSettings? settings,
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
        if (settings is not null)
            ApplyAuthentication(startInfo, settings);
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

    private static void ApplyAuthentication(ProcessStartInfo startInfo, AppSettings settings)
    {
        if (settings.GitAuthenticationMode == "https")
            return;

        if (settings.GitAuthenticationMode == "putty-key")
        {
            var puttyKeyPath = GetPrivateKeyPath(settings, "PuTTY");
            var plinkPath = FindPlinkExecutable();
            startInfo.Environment["GIT_SSH_COMMAND"] =
                $"{QuoteForShell(plinkPath.Replace('\\', '/'))} -ssh -batch -i {QuoteForShell(puttyKeyPath.Replace('\\', '/'))}";
            startInfo.Environment["GIT_SSH_VARIANT"] = "plink";
            return;
        }

        var sshExecutable = "ssh";
        if (settings.GitAuthenticationMode == "ssh-agent")
        {
            var windowsDirectory = Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows";
            var windowsSsh = Path.Combine(windowsDirectory, "System32", "OpenSSH", "ssh.exe");
            if (!File.Exists(windowsSsh))
                throw new GitException(Localization.Text(
                    "Windows OpenSSH Client is not installed. Install it or select another authentication mode.",
                    "Клиент Windows OpenSSH не установлен. Установите его или выберите другой режим аутентификации."));
            sshExecutable = QuoteForShell(windowsSsh.Replace('\\', '/'));
        }

        var sshCommand = $"{sshExecutable} -o BatchMode=yes -o StrictHostKeyChecking=yes";
        if (settings.GitAuthenticationMode == "ssh-key")
        {
            var keyPath = GetPrivateKeyPath(settings, "SSH");
            sshCommand += $" -i {QuoteForShell(keyPath.Replace('\\', '/'))} -o IdentitiesOnly=yes";
        }

        startInfo.Environment["GIT_SSH_COMMAND"] = sshCommand;
        startInfo.Environment["GIT_SSH_VARIANT"] = "ssh";
    }

    private static string GetPrivateKeyPath(AppSettings settings, string keyType)
    {
        if (string.IsNullOrWhiteSpace(settings.SshPrivateKeyPath))
            throw new GitException(Localization.Format(
                "Select a {0} private key file.",
                "Выберите файл приватного ключа {0}.",
                keyType));
        var keyPath = Path.GetFullPath(settings.SshPrivateKeyPath);
        if (!File.Exists(keyPath))
            throw new GitException(Localization.Format(
                "Private key not found: {0}",
                "Файл приватного ключа не найден: {0}",
                keyPath));
        if (keyPath.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new GitException(Localization.Text(
                "The private key path contains invalid characters.",
                "Путь к приватному ключу содержит недопустимые символы."));
        return keyPath;
    }

    private static string FindPlinkExecutable()
    {
        var candidates = new List<string>();
        foreach (var environmentVariable in new[] { "ProgramFiles", "ProgramFiles(x86)" })
        {
            var directory = Environment.GetEnvironmentVariable(environmentVariable);
            if (!string.IsNullOrWhiteSpace(directory))
                candidates.Add(Path.Combine(directory, "PuTTY", "plink.exe"));
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        candidates.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim('"'), "plink.exe")));
        var executable = candidates.FirstOrDefault(File.Exists);
        return executable ?? throw new GitException(Localization.Text(
            "plink.exe was not found. Install PuTTY or add its folder to PATH.",
            "Файл plink.exe не найден. Установите PuTTY или добавьте его папку в PATH."));
    }

    private async Task<(string Name, string Url, string MergeReference)?> TryGetUpstreamRemoteAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        var branchResult = await RunAsync(repositoryPath, cancellationToken,
            "branch", "--show-current");
        var branch = branchResult.Output.Trim();
        if (branchResult.ExitCode != 0 || branch.Length == 0)
            return null;

        var remoteResult = await RunAsync(repositoryPath, cancellationToken,
            "config", "--get", $"branch.{branch}.remote");
        var remoteName = remoteResult.Output.Trim();
        if (remoteResult.ExitCode != 0 || remoteName.Length == 0)
            return null;
        var mergeResult = await RunAsync(repositoryPath, cancellationToken,
            "config", "--get", $"branch.{branch}.merge");
        var mergeReference = mergeResult.Output.Trim();
        if (mergeResult.ExitCode != 0 || mergeReference.Length == 0)
            return null;
        if (remoteName == ".")
            return (remoteName, ".", mergeReference);

        var urlResult = await RunAsync(repositoryPath, cancellationToken,
            "remote", "get-url", remoteName);
        if (urlResult.ExitCode != 0 || string.IsNullOrWhiteSpace(urlResult.Output))
            return null;
        return (remoteName, urlResult.Output.Trim(), mergeReference);
    }

    private static string QuoteForShell(string value) =>
        "'" + value.Replace("'", "'\"'\"'") + "'";

    private static void ValidateRemoteForAuthenticationMode(string remoteUrl, string mode)
    {
        if (mode == "https" && !remoteUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new GitException(Localization.Text(
                "HTTPS authentication requires an origin URL that starts with https://.",
                "Для HTTPS-аутентификации адрес origin должен начинаться с https://."));
        if (mode != "https" && remoteUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            throw new GitException(Localization.Text(
                "SSH authentication requires an SSH origin URL, for example git@github.com:user/repository.git.",
                "Для SSH-аутентификации нужен SSH-адрес origin, например git@github.com:user/repository.git."));
        if (mode != "https" && !IsSshRemote(remoteUrl))
            throw new GitException(Localization.Text(
                "The upstream remote does not use a supported SSH URL.",
                "Upstream remote не использует поддерживаемый SSH-адрес."));
    }

    private static bool IsSshRemote(string remoteUrl)
    {
        if (remoteUrl.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
            return true;
        if (remoteUrl.Contains("://", StringComparison.Ordinal))
            return false;
        var at = remoteUrl.IndexOf('@');
        var hostStart = at >= 0 ? at + 1 : 0;
        var colon = remoteUrl.IndexOf(':', hostStart);
        var hostLength = colon - hostStart;
        return colon > hostStart && colon < remoteUrl.Length - 1 &&
               hostLength > 1 &&
               !remoteUrl[..colon].Contains('/') &&
               !remoteUrl[..colon].Contains('\\') &&
               !remoteUrl.Any(char.IsWhiteSpace);
    }
}

public sealed record GitResult(int ExitCode, string Output, string Error);

public sealed class GitException : Exception
{
    public GitException(string message) : base(message) { }
    public GitException(string message, Exception innerException) : base(message, innerException) { }
}
