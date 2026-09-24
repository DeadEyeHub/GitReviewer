using GitReviewer.Models;

namespace GitReviewer.Services;

public sealed class ReviewRunner
{
    private readonly GitService _git;
    private readonly ModelClient _model;
    private readonly ConfigurationStore _configuration;
    private readonly StateStore _stateStore;
    private readonly ReportWriter _reportWriter;
    private readonly object _lifecycleLock = new();
    private CancellationTokenSource? _cancellation;
    private Task? _runTask;
    private Task? _stopTask;
    private bool _manualReviewActive;
    private string _activeCommit = string.Empty;

    public event Action<string>? Log;
    public event Action<string>? ModelLog;
    public event Action<string>? StatusChanged;
    public event Action<string>? CommitChanged;
    public event Action<ReviewProgress>? Progress;
    public event Action<CommitReviewed>? Reviewed;
    public bool IsRunning
    {
        get
        {
            lock (_lifecycleLock)
                return _runTask is { IsCompleted: false };
        }
    }

    public ReviewRunner(
        GitService git,
        ModelClient model,
        ConfigurationStore configuration,
        StateStore stateStore,
        ReportWriter reportWriter)
    {
        _git = git;
        _model = model;
        _configuration = configuration;
        _stateStore = stateStore;
        _reportWriter = reportWriter;
    }

    public bool Start(AppSettings settings, ModelProfile profile)
    {
        lock (_lifecycleLock)
        {
            if (_runTask is { IsCompleted: false } || _stopTask is not null || _manualReviewActive)
                return false;

            _cancellation = new CancellationTokenSource();
            _runTask = RunLoopAsync(settings, profile.Clone(), _cancellation.Token);
            return true;
        }
    }

    public Task StopAsync()
    {
        lock (_lifecycleLock)
        {
            if (_stopTask is not null)
                return _stopTask;
            if (_cancellation is null || _runTask is null)
                return Task.CompletedTask;

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopTask = completion.Task;
            _cancellation.Cancel();
            _ = FinishStopAsync(_cancellation, _runTask, completion);
            return completion.Task;
        }
    }

    public async Task<ReviewResult> ReviewSingleCommitAsync(
        AppSettings settings,
        ModelProfile profile,
        string revision,
        CancellationToken cancellationToken)
    {
        lock (_lifecycleLock)
        {
            if (_runTask is { IsCompleted: false } || _stopTask is not null || _manualReviewActive)
                throw new InvalidOperationException(Localization.Text(
                    "Stop automatic monitoring before reviewing a selected commit.",
                    "Остановите автоматическую проверку перед проверкой выбранного коммита."));
            _manualReviewActive = true;
        }

        try
        {
            _activeCommit = string.Empty;
            var repositoryPath = Path.GetFullPath(settings.RepositoryPath);
            await _git.ValidateRepositoryAsync(repositoryPath, cancellationToken);
            var identity = await _git.GetRepositoryIdentityAsync(repositoryPath, cancellationToken);
            repositoryPath = identity.WorkTreeRoot;
            var branch = await _git.ResolveBranchAsync(repositoryPath, settings.BranchRef, cancellationToken);
            var sha = await _git.ResolveCommitAsync(repositoryPath, revision, cancellationToken);
            var result = await AnalyzeAndReportAsync(
                repositoryPath, identity.CommonGitDirectory, branch, sha, profile.Clone(), true, cancellationToken);
            Complete(repositoryPath, branch, sha, profile, result, true, cancellationToken);
            StatusChanged?.Invoke(Localization.Text("Manual review completed", "Ручная проверка завершена"));
            Log?.Invoke(Localization.Format(
                "Selected commit {0} reviewed, findings: {1}.",
                "Выбранный коммит {0} проверен, замечаний: {1}.",
                Short(sha), result.Findings.Count));
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Emit(ReviewStage.Canceled, profile, _activeCommit);
            throw;
        }
        catch
        {
            Emit(ReviewStage.Failed, profile, _activeCommit);
            throw;
        }
        finally
        {
            lock (_lifecycleLock)
                _manualReviewActive = false;
        }
    }

    private async Task FinishStopAsync(
        CancellationTokenSource cancellation,
        Task runTask,
        TaskCompletionSource completion)
    {
        try
        {
            await runTask;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Log?.Invoke(Localization.Format(
                "Error while stopping: {0}",
                "Ошибка при остановке: {0}",
                exception.Message));
        }
        finally
        {
            cancellation.Dispose();
            lock (_lifecycleLock)
            {
                _cancellation = null;
                _runTask = null;
                _stopTask = null;
            }
            StatusChanged?.Invoke(Localization.Text("Stopped", "Остановлено"));
            completion.SetResult();
        }
    }

    private async Task RunLoopAsync(AppSettings settings, ModelProfile profile, CancellationToken cancellationToken)
    {
        Log?.Invoke(Localization.Text("Review started.", "Проверка запущена."));
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _activeCommit = string.Empty;
                await RunCycleAsync(settings, profile, cancellationToken);
                StatusChanged?.Invoke(Localization.Format(
                    "Waiting {0} sec.",
                    "Ожидание {0} сек.",
                    settings.PollIntervalSeconds));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Emit(ReviewStage.Canceled, profile, _activeCommit);
                throw;
            }
            catch (Exception exception)
            {
                Emit(ReviewStage.Failed, profile, _activeCommit, exception.Message);
                StatusChanged?.Invoke(Localization.Text("Error", "Ошибка"));
                Log?.Invoke(Localization.Format("Error: {0}", "Ошибка: {0}", exception.Message));
            }

            await Task.Delay(TimeSpan.FromSeconds(settings.PollIntervalSeconds), cancellationToken);
        }
    }

    private async Task RunCycleAsync(
        AppSettings settings,
        ModelProfile profile,
        CancellationToken cancellationToken)
    {
        var repositoryPath = Path.GetFullPath(settings.RepositoryPath);
        await _git.ValidateRepositoryAsync(repositoryPath, cancellationToken);
        var identity = await _git.GetRepositoryIdentityAsync(repositoryPath, cancellationToken);
        repositoryPath = identity.WorkTreeRoot;
        var branch = await _git.ResolveBranchAsync(repositoryPath, settings.BranchRef, cancellationToken);
        await _stateStore.RegisterRepositoryAsync(
            identity.CommonGitDirectory, repositoryPath, cancellationToken);
        var position = await _stateStore.GetReviewPositionAsync(
            identity.CommonGitDirectory, branch, cancellationToken);
        var previousCommit = position.Cursor;
        string? fetchedTarget = null;

        if (settings.PullEnabled)
        {
            StatusChanged?.Invoke(Localization.Text("Running git fetch", "Выполняется git fetch"));
            var fetchSettings = new AppSettings
            {
                BranchRef = branch,
                GitAuthenticationMode = settings.GitAuthenticationMode,
                SshPrivateKeyPath = settings.SshPrivateKeyPath,
                PlinkPath = settings.PlinkPath
            };
            var fetch = await _git.FetchAsync(repositoryPath, fetchSettings, cancellationToken,
                message => Publish(Log, message));
            Publish(ModelLog, GitService.SanitizeDiagnostic($"Git fetch exit code: {fetch.ExitCode}\nstdout: {fetch.Output}\nstderr: {fetch.Error}"));
            if (fetch.ExitCode != 0)
            {
                var details = string.IsNullOrWhiteSpace(fetch.Error) ? fetch.Output : fetch.Error;
                if (string.IsNullOrWhiteSpace(details))
                    details = Localization.Text("Git returned no error output.", "Git не вернул текст ошибки.");
                throw new GitException(Localization.Format(
                    "Git fetch failed with exit code {0}: {1}",
                    "Git fetch не выполнен, код завершения {0}: {1}",
                    fetch.ExitCode,
                    GitService.SanitizeDiagnostic(details.Trim())));
            }
            fetchedTarget = fetch.FetchedCommit;
        }

        if (fetchedTarget is not null && branch.StartsWith("refs/heads/", StringComparison.Ordinal))
        {
            var localHead = await _git.GetBranchHeadAsync(repositoryPath, branch, cancellationToken);
            var advancing = await _git.GetAdvanceCommitsAsync(repositoryPath, localHead, fetchedTarget, cancellationToken);
            if (advancing.Count > 0)
                await _git.ValidateAdvanceAsync(repositoryPath, branch, localHead, cancellationToken);
            if (position.PendingStart is not null)
            {
                if (!await _git.IsAncestorAsync(repositoryPath, position.PendingStart, localHead, cancellationToken))
                    throw new GitException("Selected start commit must be an ancestor of the local branch tip.");
                await ProcessCommitAsync(repositoryPath, identity.CommonGitDirectory, branch, position.PendingStart, profile, cancellationToken);
                foreach (var oldSha in await _git.GetCommitsAfterAsync(repositoryPath, position.PendingStart, localHead, cancellationToken))
                    await ProcessCommitAsync(repositoryPath, identity.CommonGitDirectory, branch, oldSha, profile, cancellationToken);
            }
            Publish(Log, Localization.Format("Local baseline {0}; fetched tip {1}; commits to review and advance: {2}.",
                "Локальная точка старта {0}; полученная вершина {1}; коммитов для проверки и продвижения: {2}.", Short(localHead), Short(fetchedTarget), advancing.Count));
            foreach (var nextSha in advancing)
            {
                await ProcessCommitAsync(repositoryPath, identity.CommonGitDirectory, branch, nextSha, profile, cancellationToken, localHead);
                localHead = nextSha;
            }
            return;
        }

        if (position.PendingStart is not null)
        {
            var pendingHead = await _git.GetBranchHeadAsync(repositoryPath, branch, cancellationToken);
            if (!await _git.IsAncestorAsync(repositoryPath, position.PendingStart, pendingHead, cancellationToken))
                throw new GitException(Localization.Text(
                    "The selected start commit is not an ancestor of the selected branch tip.",
                    "Выбранный стартовый коммит не является предком вершины выбранной ветки."));
            Log?.Invoke(Localization.Format(
                "Starting automatic review with selected commit {0}.",
                "Автоматическая проверка начинается с выбранного коммита {0}.",
                Short(position.PendingStart)));
            await ProcessCommitAsync(repositoryPath, identity.CommonGitDirectory,
                branch, position.PendingStart, profile, cancellationToken);
            previousCommit = position.PendingStart;
        }
        else if (previousCommit is null)
        {
            var initialHead = await _git.GetBranchHeadAsync(repositoryPath, branch, cancellationToken);
            Log?.Invoke(Localization.Format(
                "First connection: reviewing current commit {0}.",
                "Первое подключение: проверяется текущий коммит {0}.",
                Short(initialHead)));
            await ProcessCommitAsync(repositoryPath, identity.CommonGitDirectory,
                branch, initialHead, profile, cancellationToken);
            previousCommit = initialHead;
        }

        var head = await _git.GetBranchHeadAsync(repositoryPath, branch, cancellationToken);
        if (head.Equals(previousCommit, StringComparison.Ordinal))
        {
            Publish(Log, Localization.Format("No new commits on {0}; tip {1}.", "Новых коммитов в {0} нет; вершина {1}.", branch, Short(head)));
            return;
        }

        if (!await _git.IsAncestorAsync(repositoryPath, previousCommit, head, cancellationToken))
            throw new GitException(Localization.Text(
                "History changed: the saved commit is not an ancestor of the selected branch tip.",
                "История изменена: сохраненный коммит не является предком вершины выбранной ветки."));

        var commits = await _git.GetCommitsAfterAsync(repositoryPath, previousCommit, head, cancellationToken);
        Publish(Log, Localization.Format("Found {0} new commits on {1}.", "Найдено новых коммитов: {0}, ветка {1}.", commits.Count, branch));
        foreach (var sha in commits)
            await ProcessCommitAsync(repositoryPath, identity.CommonGitDirectory,
                branch, sha, profile, cancellationToken);
    }

    private async Task ProcessCommitAsync(
        string repositoryPath,
        string repositoryIdentity,
        string branch,
        string sha,
        ModelProfile profile,
        CancellationToken cancellationToken,
        string? advanceFrom = null)
    {
        var result = await AnalyzeAndReportAsync(
            repositoryPath, repositoryIdentity, branch, sha, profile, false, cancellationToken);
        Emit(ReviewStage.SavingCursor, profile, sha);
        await _stateStore.SetCursorAsync(repositoryIdentity, branch, sha, cancellationToken);
        if (advanceFrom is not null)
        {
            await _git.AdvanceBranchAsync(repositoryPath, branch, advanceFrom, sha, cancellationToken);
            Publish(Log, Localization.Format("Local branch {0} advanced: {1} → {2}.",
                "Локальная ветка {0} продвинута: {1} → {2}.", branch, Short(advanceFrom), Short(sha)));
        }
        Complete(repositoryPath, branch, sha, profile, result, false, cancellationToken);
        Log?.Invoke(Localization.Format(
            "Commit {0} reviewed, findings: {1}.",
            "Коммит {0} проверен, замечаний: {1}.",
            Short(sha), result.Findings.Count));
    }

    private async Task<ReviewResult> AnalyzeAndReportAsync(
        string repositoryPath,
        string repositoryIdentity,
        string branch,
        string sha,
        ModelProfile profile,
        bool manualReview,
        CancellationToken cancellationToken)
    {
        _activeCommit = sha;
        StatusChanged?.Invoke(Localization.Format("Reviewing {0}", "Проверяется {0}", Short(sha)));
        CommitChanged?.Invoke(Short(sha));
        Emit(ReviewStage.Started, profile, sha);

        var commit = await _git.GetCommitInfoAsync(repositoryPath, sha, cancellationToken);
        Emit(ReviewStage.PreparingDiff, profile, sha);
        var tools = await GitToolSession.CreateAsync(repositoryPath, sha, cancellationToken);
        var result = new ReviewResult { EmptyDiff = await tools.IsEmptyAsync(cancellationToken) };
        if (!result.EmptyDiff)
        {
            var response = await _model.ReviewAsync(profile, tools, branch, _configuration.LoadPrompt(), cancellationToken,
                stage => Emit(stage, profile, sha), message =>
                    Publish(ModelLog, message), (stage, detail) => Emit(stage, profile, sha, detail));
            Emit(ReviewStage.Parsing, profile, sha);
            result = ReviewParser.Parse(response);
        }

        Emit(ReviewStage.Report, profile, sha);
        await _reportWriter.AppendAsync(
            repositoryPath, branch, commit, result, manualReview, cancellationToken, repositoryIdentity);
        return result;
    }

    private static string Short(string sha) => sha[..Math.Min(8, sha.Length)];

    private void Emit(ReviewStage stage, ModelProfile profile, string sha, string detail = "") =>
        Publish(Progress, new ReviewProgress(stage, profile.Model, sha, detail));

    private void Complete(string path, string branch, string sha, ModelProfile profile,
        ReviewResult result, bool manual, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Emit(ReviewStage.Completed, profile, sha);
        Publish(Reviewed, new CommitReviewed(path, branch, sha, result.Findings.Count,
            result.UnstructuredResponse is not null, result.EmptyDiff, manual));
    }

    private static void Publish<T>(Action<T>? handlers, T value)
    {
        if (handlers is null) return;
        foreach (Action<T> handler in handlers.GetInvocationList())
        {
            // Observers must not turn a persisted review into a failed operation.
            try { handler(value); }
            catch { }
        }
    }
}
