using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using GitReviewer.Models;

namespace GitReviewer.Services;

public sealed class GitService
{
    public event Action<string>? Diagnostic;

    private void PublishDiagnostic(GitCommandTrace trace)
        => WriteDiagnostic(System.Text.Json.JsonSerializer.Serialize(trace with
        {
            Arguments = trace.Arguments.Select(SanitizeDiagnostic).ToArray(),
            Failure = trace.Failure is null ? null : SanitizeDiagnostic(trace.Failure),
            Result = trace.Result is null ? null : trace.Result with
            {
                Output = SanitizeDiagnostic(trace.Result.Output),
                Error = SanitizeDiagnostic(trace.Result.Error)
            }
        }));

    private void WriteDiagnostic(string message)
    {
        var text = SanitizeDiagnostic(message);
        if (Diagnostic is null) return;
        foreach (Action<string> handler in Diagnostic.GetInvocationList())
            try { handler("Git operation: " + text); } catch { }
    }

    internal static string SanitizeDiagnostic(string text) =>
        System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(text,
                @"(?i)(https?://)[^\s/\""<>]*@", "$1[redacted]@"),
            @"(?i)([?&](?:token|access_token|password|key|signature)=)[^\s&\""<>]*", "$1[redacted]");
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FetchLocks =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public async Task<GitRepositoryIdentity> GetRepositoryIdentityAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        var output = await RunRequiredAsync(repositoryPath, cancellationToken,
            "rev-parse", "--path-format=absolute", "--show-toplevel", "--absolute-git-dir", "--git-common-dir");
        var paths = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (paths.Length != 3 || paths.Any(path => path.Length == 0 || path.Any(char.IsControl)))
            throw new GitException(Localization.Text(
                "Git returned invalid worktree information.",
                "Git вернул некорректные сведения о worktree."));
        return new GitRepositoryIdentity(
            NormalizePath(paths[0]),
            NormalizePath(paths[1]),
            NormalizePath(paths[2]));
    }

    public async Task<string> GetRepositoryRootAsync(string repositoryPath, CancellationToken cancellationToken)
        => (await GetRepositoryIdentityAsync(repositoryPath, cancellationToken)).WorkTreeRoot;

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

    public async Task<IReadOnlyList<string>> GetBranchesAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        var output = await RunRequiredAsync(repositoryPath, cancellationToken,
            "for-each-ref", "--format=%(refname)%09%(symref)", "refs/heads/", "refs/remotes/");
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split('\t'))
            .Where(parts => parts.Length == 2 && parts[1].Length == 0)
            .Select(parts => parts[0]).ToArray();
    }

    public async Task<string> ResolveBranchAsync(string repositoryPath, string selectedRef, CancellationToken cancellationToken)
    {
        if (selectedRef.Length == 0)
            selectedRef = (await RunRequiredAsync(repositoryPath, cancellationToken, "symbolic-ref", "--quiet", "HEAD")).Trim();
        if (!(await GetBranchesAsync(repositoryPath, cancellationToken)).Contains(selectedRef, StringComparer.Ordinal))
            throw new GitException(Localization.Format("Selected branch does not exist: {0}", "Выбранная ветка не существует: {0}", selectedRef));
        return selectedRef;
    }

    public async Task<string> GetBranchHeadAsync(string repositoryPath, string branchRef, CancellationToken cancellationToken)
    {
        branchRef = await ResolveBranchAsync(repositoryPath, branchRef, cancellationToken);
        return (await RunRequiredAsync(repositoryPath, cancellationToken,
            "rev-parse", "--verify", "--end-of-options", $"{branchRef}^{{commit}}")).Trim();
    }

    public async Task<IReadOnlyList<CommitChoice>> GetRecentCommitsAsync(
        string repositoryPath, string branch, CancellationToken cancellationToken)
    {
        var head = await GetBranchHeadAsync(repositoryPath, branch, cancellationToken);
        var result = await ReadPageAsync(repositoryPath, 0, 256_000, cancellationToken,
            "log", "--no-show-signature", "-n", "100", "--format=%H%x09%s", head, "--");
        if (result.ExitCode != 0 || result.HasMore)
            throw new GitException(Localization.Text("Cannot load the recent commit list.", "Не удалось загрузить список последних коммитов."));
        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split('\t', 2))
            .Where(parts => parts.Length == 2 && parts[0].Length == 40 && parts[0].All(Uri.IsHexDigit))
            .Select(parts => new CommitChoice(parts[0], new string(parts[1].Select(c => char.IsControl(c) ? ' ' : c).Take(300).ToArray())))
            .ToArray();
    }

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

    public async Task<GitResult> FetchAsync(
        string repositoryPath,
        AppSettings settings,
        CancellationToken cancellationToken,
        Action<string>? activity = null)
    {
        var identity = await GetRepositoryIdentityAsync(repositoryPath, cancellationToken);
        var branch = await ResolveBranchAsync(repositoryPath, settings.BranchRef, cancellationToken);
        var remote = await TryGetUpstreamRemoteAsync(repositoryPath, cancellationToken, branch);
        // Local-only repositories are valid review targets. Treat fetch as a no-op
        // when the selected branch has no upstream instead of making a remote mandatory.
        if (remote is null)
        {
            activity?.Invoke(Localization.Format("Fetch skipped: {0} has no upstream.", "Fetch пропущен: у {0} нет upstream.", branch));
            return new GitResult(0, string.Empty, string.Empty);
        }
        var resolvedRemote = remote.Value;
        WriteDiagnostic($"Fetch target: remote={resolvedRemote.Name}; url={resolvedRemote.Url}; ref={resolvedRemote.MergeReference}; branch={branch}; repository={repositoryPath}; auth={settings.GitAuthenticationMode}");
        if (resolvedRemote.Name == ".")
        {
            activity?.Invoke(Localization.Text("Fetch skipped: upstream is in this local repository.", "Fetch пропущен: upstream находится в этом локальном репозитории."));
            return new GitResult(0, string.Empty, string.Empty);
        }
        var before = await GetBranchHeadAsync(repositoryPath, branch, cancellationToken);
        activity?.Invoke(Localization.Text("Waiting for shared repository fetch lock.", "Ожидание доступа к общему Git-хранилищу для fetch."));
        var fetchLock = FetchLocks.GetOrAdd(identity.CommonGitDirectory, _ => new SemaphoreSlim(1, 1));
        await fetchLock.WaitAsync(cancellationToken);
        try
        {
            if (resolvedRemote.Url != ".")
                ValidateRemoteForAuthenticationMode(resolvedRemote.Url, settings.GitAuthenticationMode);
            // All linked worktrees use the common object store. Serializing fetches by
            // git-common-dir avoids competing ref/object updates while Git reuses objects.
            activity?.Invoke(Localization.Format("Fetching {0} from remote {1}.", "Fetch: получение {0} из remote {1}.", resolvedRemote.MergeReference, resolvedRemote.Name));
            GitResult result;
            if (branch.StartsWith("refs/heads/", StringComparison.Ordinal))
                result = await RunWithAuthenticationAsync(repositoryPath, IsLocalRemote(resolvedRemote.Url) ? null : settings, cancellationToken,
                    "fetch", "--no-tags", "--no-prune", "--no-prune-tags", "--no-recurse-submodules", "--refmap=", "--", resolvedRemote.Name, resolvedRemote.MergeReference);
            else
                result = await RunWithAuthenticationAsync(
                    repositoryPath, IsLocalRemote(resolvedRemote.Url) ? null : settings, cancellationToken, "fetch", "--no-tags", "--no-prune", "--no-prune-tags", "--no-recurse-submodules",
                    "--refmap=", "--", resolvedRemote.Name, $"+{resolvedRemote.MergeReference}:{branch}");
            if (result.ExitCode == 0)
            {
                var after = await GetBranchHeadAsync(repositoryPath, branch, cancellationToken);
                activity?.Invoke(before == after
                    ? Localization.Format("Fetch completed: {0} unchanged at {1}.", "Fetch выполнен: {0} без изменений, {1}.", branch, after[..8])
                    : Localization.Format("Fetch completed: {0}, {1} → {2}.", "Fetch выполнен: {0}, {1} → {2}.", branch, before[..8], after[..8]));
                if (branch.StartsWith("refs/heads/", StringComparison.Ordinal))
                    activity?.Invoke(Localization.Text("Fetched into FETCH_HEAD. Local branch and working files are unchanged; select refs/remotes/... to review remote updates.", "Данные получены в FETCH_HEAD. Локальная ветка и рабочие файлы не изменены; для проверки удалённых обновлений выберите refs/remotes/...."));
            }
            return result;
        }
        finally
        {
            fetchLock.Release();
        }
    }

    public async Task<string> GetSelectedRemoteSummaryAsync(string path, string branch, CancellationToken token)
    {
        var remote = await TryGetUpstreamRemoteAsync(path, token, branch);
        return remote is null ? string.Empty : $"{remote.Value.Name} | {remote.Value.MergeReference}";
    }

    public async Task TestRemoteAccessAsync(
        string repositoryPath,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        await ValidateRepositoryAsync(repositoryPath, cancellationToken);
        var branch = await ResolveBranchAsync(repositoryPath, settings.BranchRef, cancellationToken);
        var remote = await TryGetUpstreamRemoteAsync(repositoryPath, cancellationToken, branch);
        // Repository and branch validation above is sufficient for a local-only
        // repository; there is no remote access to test in that case.
        if (remote is null)
            return;
        var resolvedRemote = remote.Value;
        WriteDiagnostic($"Connection test: remote={resolvedRemote.Name}; url={resolvedRemote.Url}; ref={resolvedRemote.MergeReference}; repository={repositoryPath}; auth={settings.GitAuthenticationMode}");
        if (resolvedRemote.Url != ".")
            ValidateRemoteForAuthenticationMode(resolvedRemote.Url, settings.GitAuthenticationMode);

        var result = await RunWithAuthenticationAsync(repositoryPath, IsLocalRemote(resolvedRemote.Url) ? null : settings, cancellationToken,
            "ls-remote", "--exit-code", resolvedRemote.Name, resolvedRemote.MergeReference);
        if (result.ExitCode != 0)
            throw new GitException(result.Error.Length > 0
                ? result.Error.Trim()
                : Localization.Text(
                    "Could not access the remote repository.",
                    "Не удалось получить доступ к удаленному репозиторию."));
    }

    public async Task<string> DetectAuthenticationModeAsync(
        string repositoryPath,
        CancellationToken cancellationToken, string selectedRef = "")
    {
        var branch = await ResolveBranchAsync(repositoryPath, selectedRef, cancellationToken);
        var remote = await TryGetUpstreamRemoteAsync(repositoryPath, cancellationToken, branch);
        if (remote is null) return "ssh-agent";

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
        if (sha.Length != 40 || !sha.All(Uri.IsHexDigit)) throw new GitException("A full commit SHA is required.");
        var metadata = await ReadPageAsync(repositoryPath, 0, 16_000, cancellationToken,
            "show", "--no-show-signature", "-s", $"--format={format}", sha, "--");
        if (metadata.ExitCode != 0 || metadata.HasMore) throw new GitException("Commit metadata unavailable or exceeds output limit.");
        var output = metadata.Output.Trim();
        var parts = output.Split('\x1f', 4);
        if (parts.Length != 4 || !DateTimeOffset.TryParse(parts[2], CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var date))
            throw new GitException(Localization.Format(
                "Could not read commit data for {0}.",
                "Не удалось прочитать данные коммита {0}.",
                sha));
        return new CommitInfo(parts[0], parts[1], date, parts[3]);
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
        => await RunCoreAsync(repositoryPath, null, cancellationToken, null, null, arguments);

    private async Task<GitResult> RunWithAuthenticationAsync(
        string repositoryPath,
        AppSettings? settings,
        CancellationToken cancellationToken,
        params string[] arguments)
        => await RunCoreAsync(repositoryPath, settings, cancellationToken, null, PublishDiagnostic, arguments);

    internal static Task<GitResult> ReadPageAsync(string repositoryPath, int offset, int limit,
        CancellationToken token, params string[] arguments) =>
        RunCoreAsync(repositoryPath, null, token, (offset, limit), null, arguments);

    internal static Task<GitResult> ReadPageAsync(string repositoryPath, int offset, int limit,
        CancellationToken token, Action<GitCommandTrace>? trace, params string[] arguments) =>
        RunCoreAsync(repositoryPath, null, token, (offset, limit), trace, arguments);

    private static async Task<GitResult> RunCoreAsync(
        string repositoryPath,
        AppSettings? settings,
        CancellationToken cancellationToken,
        (int Offset, int Limit)? page,
        Action<GitCommandTrace>? trace,
        params string[] arguments)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timeoutSeconds = page is null ? 120 : 30;
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var elapsed = Stopwatch.StartNew();
        var partialError = new System.Text.StringBuilder();
        var operation = arguments.FirstOrDefault() ?? "unknown";
        var token = timeout.Token;
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = repositoryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (page is not null)
        {
            foreach (var key in startInfo.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.Ordinal)).ToArray())
                startInfo.Environment.Remove(key);
            startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
            startInfo.Environment["GIT_NO_LAZY_FETCH"] = "1";
            startInfo.Environment["GIT_NO_REPLACE_OBJECTS"] = "1";
            startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
            foreach (var argument in new[] { "--no-pager", "--literal-pathspecs", "-c", "protocol.allow=never", "-c", "core.hooksPath=/dev/null" })
                startInfo.ArgumentList.Add(argument);
        }
        if (settings is not null)
            ApplyAuthentication(startInfo, settings);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        void PublishTrace(string stage, int? processExitCode = null, GitResult? result = null, string? failure = null)
        {
            if (trace is null) return;
            try
            {
                trace(new GitCommandTrace(
                    stage,
                    startInfo.FileName,
                    startInfo.WorkingDirectory,
                    startInfo.ArgumentList.ToArray(),
                    page?.Offset,
                    page?.Limit,
                    processExitCode,
                    result,
                    $"{failure} elapsed={elapsed.Elapsed.TotalSeconds:F1}s; timeout={timeoutSeconds}s; auth={settings?.GitAuthenticationMode ?? "local"}"));
            }
            catch
            {
                // Diagnostics must not affect the review operation.
            }
        }

        using var process = new Process { StartInfo = startInfo };
        token.ThrowIfCancellationRequested();
        PublishTrace("started");
        try
        {
            if (!process.Start())
            {
                PublishTrace("start_failed", failure: "Could not start Git.");
                throw new GitException(Localization.Text(
                    "Could not start Git.", "Не удалось запустить Git."));
            }
        }
        catch (Exception exception) when (exception is not GitException)
        {
            PublishTrace("start_failed", failure: exception.Message);
            throw new GitException(Localization.Text(
                "Git was not found or could not be started.",
                "Git не найден или не может быть запущен.") +
                $" Operation: {operation}; repository: {repositoryPath}; reason: {SanitizeDiagnostic(exception.Message)}", exception);
        }

        try
        {
            var more = false;
            async Task<string> ReadBoundedAsync(StreamReader reader, int skip, int limit, bool paging)
            {
                try
                {
                    var text = new System.Text.StringBuilder();
                    var buffer = new char[4096];
                    while (true)
                    {
                        var count = await reader.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length,
                            skip > 0 ? skip : limit - text.Length + 1)), token);
                        if (count == 0)
                        {
                            if (skip > 0) throw new GitException("Git output ended before the requested offset; review is incomplete.");
                            return text.ToString();
                        }
                        if (skip > 0) { skip -= count; continue; }
                        if (ReferenceEquals(reader, process.StandardError) && partialError.Length < 16_000)
                            partialError.Append(buffer, 0, Math.Min(count, 16_000 - partialError.Length));
                        var accepted = Math.Min(count, limit - text.Length);
                        text.Append(buffer, 0, accepted);
                        if (accepted == count) continue;
                        if (!paging) throw new GitException("Git output limit exceeded; review is incomplete.");
                        more = true;
                        // Keep a Unicode scalar intact across character-offset pages.
                        if (text.Length > 0 && char.IsHighSurrogate(text[text.Length - 1])) text.Length--;
                        if (!process.HasExited) process.Kill(true);
                        return text.ToString();
                    }
                }
                catch
                {
                    if (!process.HasExited) process.Kill(true);
                    throw;
                }
            }
            var outputTask = ReadBoundedAsync(process.StandardOutput, page?.Offset ?? 0, page?.Limit ?? 4_000_000, page is not null);
            var errorTask = ReadBoundedAsync(process.StandardError, 0, 16_000, false);
            await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync(token));
            var result = new GitResult(more ? 0 : process.ExitCode, await outputTask, await errorTask, more);
            PublishTrace("completed", process.ExitCode, result);
            return result;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(true);
            if (!cancellationToken.IsCancellationRequested)
            {
                var stderr = partialError.Length == 0 ? "Git produced no stderr before timeout." : SanitizeDiagnostic(partialError.ToString());
                var context = SanitizeDiagnostic(string.Join(" ", arguments));
                var failure = $"Git {operation} timed out after {elapsed.Elapsed.TotalSeconds:F1}s (limit {timeoutSeconds}s). Repository: {repositoryPath}. Command: git {context}. Authentication: {settings?.GitAuthenticationMode ?? "local"}. stderr: {stderr}";
                PublishTrace("timed_out", TryGetExitCode(process), failure: failure);
                throw new GitException(failure);
            }
            PublishTrace("canceled", TryGetExitCode(process), failure: "Git command was canceled.");
            throw;
        }
        catch (Exception exception)
        {
            PublishTrace("failed", TryGetExitCode(process), failure: exception.Message);
            throw;
        }
    }

    private static int? TryGetExitCode(Process process)
    {
        try { return process.HasExited ? process.ExitCode : null; }
        catch (InvalidOperationException) { return null; }
    }

    private static void ApplyAuthentication(ProcessStartInfo startInfo, AppSettings settings)
    {
        if (settings.GitAuthenticationMode == "https")
            return;

        if (settings.GitAuthenticationMode == "putty-key")
        {
            var puttyKeyPath = GetPrivateKeyPath(settings, "PuTTY");
            var plinkPath = FindPlinkExecutable(settings.PlinkPath);
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

    private static string FindPlinkExecutable(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (configuredPath.IndexOfAny(['\r', '\n', '\0']) >= 0)
                throw new GitException(Localization.Text(
                    "The Plink path contains invalid characters.",
                    "Путь к Plink содержит недопустимые символы."));
            var fullPath = Path.GetFullPath(configuredPath.Trim());
            if (!File.Exists(fullPath))
                throw new GitException(Localization.Format(
                    "Plink executable not found: {0}",
                    "Исполняемый файл Plink не найден: {0}", fullPath));
            return fullPath;
        }

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
            "plink.exe was not found. Select its path, install PuTTY, or add its folder to PATH.",
            "Файл plink.exe не найден. Укажите путь, установите PuTTY или добавьте его папку в PATH."));
    }

    private async Task<(string Name, string Url, string MergeReference)?> TryGetUpstreamRemoteAsync(
        string repositoryPath,
        CancellationToken cancellationToken, string branchRef)
    {
        // Git supplies the remote and source ref, including custom fetch refspecs.
        if (branchRef.StartsWith("refs/remotes/", StringComparison.Ordinal))
        {
            var remotes = (await RunRequiredAsync(repositoryPath, cancellationToken, "remote"))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var name in remotes.OrderByDescending(value => value.Length))
            {
                var specs = await RunAsync(repositoryPath, cancellationToken, "config", "--get-all", $"remote.{name}.fetch");
                foreach (var spec in specs.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var parts = spec.TrimStart('+').Split(':');
                    if (parts.Length != 2 || !parts[0].StartsWith("refs/heads/", StringComparison.Ordinal)) continue;
                    string? source = null;
                    var star = parts[1].IndexOf('*');
                    if (star < 0 && parts[1] == branchRef) source = parts[0];
                    else if (star >= 0 && branchRef.StartsWith(parts[1][..star], StringComparison.Ordinal) &&
                             branchRef.EndsWith(parts[1][(star + 1)..], StringComparison.Ordinal) &&
                             branchRef.Length >= parts[1].Length - 1)
                        source = parts[0].Replace("*", branchRef.Substring(star, branchRef.Length - parts[1].Length + 1));
                    if (source is not null)
                        return (name, (await RunRequiredAsync(repositoryPath, cancellationToken, "remote", "get-url", name)).Trim(), source);
                }
            }
            return null;
        }
        var branch = branchRef["refs/heads/".Length..];

        var remoteResult = await RunAsync(repositoryPath, cancellationToken,
            "config", "--get", $"branch.{branch}.remote");
        var remoteName = remoteResult.Output.Trim();
        if (remoteResult.ExitCode != 0 || remoteName.Length == 0)
            return null;
        var mergeResult = await RunAsync(repositoryPath, cancellationToken,
            "config", "--get", $"branch.{branch}.merge");
        var mergeReference = mergeResult.Output.Trim();
        if (mergeResult.ExitCode != 0 || !mergeReference.StartsWith("refs/heads/", StringComparison.Ordinal))
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
        if (IsLocalRemote(remoteUrl)) return;
        if (mode == "https" && !remoteUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new GitException(Localization.Text(
                "HTTPS authentication requires a selected remote URL that starts with https://.",
                "Для HTTPS-аутентификации адрес выбранного remote должен начинаться с https://."));
        if (mode != "https" && remoteUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            throw new GitException(Localization.Text(
                "SSH authentication requires an SSH remote URL, for example git@github.com:user/repository.git.",
                "Для SSH-аутентификации нужен SSH-адрес remote, например git@github.com:user/repository.git."));
        if (mode != "https" && !IsSshRemote(remoteUrl))
            throw new GitException(Localization.Text(
                "The upstream remote does not use a supported SSH URL.",
                "Upstream remote не использует поддерживаемый SSH-адрес."));
    }

    internal static bool IsLocalRemote(string remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl) || remoteUrl.Any(char.IsControl)) return false;
        if (remoteUrl.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri) && uri.IsFile;
        if (remoteUrl.Contains("://", StringComparison.Ordinal) || remoteUrl.Contains("::", StringComparison.Ordinal)) return false;
        if (OperatingSystem.IsWindows() && remoteUrl.Length >= 3 && char.IsAsciiLetter(remoteUrl[0]) &&
            remoteUrl[1] == ':' && remoteUrl[2] is '/' or '\\') return true;
        var colon = remoteUrl.IndexOf(':');
        var slash = remoteUrl.IndexOfAny(['/', '\\']);
        return colon < 0 || slash >= 0 && slash < colon;
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

    private static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}

public sealed record GitResult(int ExitCode, string Output, string Error, bool HasMore = false);

internal sealed record GitCommandTrace(
    string Stage,
    string Executable,
    string WorkingDirectory,
    string[] Arguments,
    int? Offset,
    int? Limit,
    int? ProcessExitCode,
    GitResult? Result,
    string? Failure);

public sealed class GitException : Exception
{
    public GitException(string message) : base(message) { }
    public GitException(string message, Exception innerException) : base(message, innerException) { }
}
