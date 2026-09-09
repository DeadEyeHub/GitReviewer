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
            var branch = await _git.ResolveBranchAsync(repositoryPath, settings.BranchRef, cancellationToken);
            var sha = await _git.ResolveCommitAsync(repositoryPath, revision, cancellationToken);
            var result = await AnalyzeAndReportAsync(
                repositoryPath, branch, sha, profile.Clone(), true, cancellationToken);
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
                Emit(ReviewStage.Failed, profile, _activeCommit);
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
        var branch = await _git.ResolveBranchAsync(repositoryPath, settings.BranchRef, cancellationToken);
        var stateKey = $"{repositoryPath}|{branch}";
        var state = await _stateStore.LoadAsync(cancellationToken);

        if (settings.PullEnabled)
        {
            StatusChanged?.Invoke(Localization.Text("Running git fetch", "Выполняется git fetch"));
            var fetchSettings = new AppSettings
            {
                BranchRef = branch, GitAuthenticationMode = settings.GitAuthenticationMode,
                SshPrivateKeyPath = settings.SshPrivateKeyPath, PlinkPath = settings.PlinkPath
            };
            var fetch = await _git.FetchAsync(repositoryPath, fetchSettings, cancellationToken);
            if (fetch.ExitCode != 0)
                throw new GitException(Localization.Text("Git fetch failed.", "Git fetch не выполнен."));
            Log?.Invoke(Localization.Text("Git fetch completed.", "Git fetch выполнен."));
        }

        if (!state.LastReviewedCommits.TryGetValue(stateKey, out var previousCommit))
        {
            var initialHead = await _git.GetBranchHeadAsync(repositoryPath, branch, cancellationToken);
            Log?.Invoke(Localization.Format(
                "First connection: reviewing current commit {0}.",
                "Первое подключение: проверяется текущий коммит {0}.",
                Short(initialHead)));
            await ProcessCommitAsync(repositoryPath, branch, stateKey, initialHead, state, profile, cancellationToken);
            previousCommit = initialHead;
        }

        var head = await _git.GetBranchHeadAsync(repositoryPath, branch, cancellationToken);
        if (head.Equals(previousCommit, StringComparison.Ordinal))
            return;

        if (!await _git.IsAncestorAsync(repositoryPath, previousCommit, head, cancellationToken))
            throw new GitException(Localization.Text(
                "History changed: the saved commit is not an ancestor of the selected branch tip.",
                "История изменена: сохраненный коммит не является предком вершины выбранной ветки."));

        var commits = await _git.GetCommitsAfterAsync(repositoryPath, previousCommit, head, cancellationToken);
        foreach (var sha in commits)
            await ProcessCommitAsync(repositoryPath, branch, stateKey, sha, state, profile, cancellationToken);
    }

    private async Task ProcessCommitAsync(
        string repositoryPath,
        string branch,
        string stateKey,
        string sha,
        RepositoryState state,
        ModelProfile profile,
        CancellationToken cancellationToken)
    {
        var result = await AnalyzeAndReportAsync(repositoryPath, branch, sha, profile, false, cancellationToken);
        state.LastReviewedCommits[stateKey] = sha;
        Emit(ReviewStage.SavingCursor, profile, sha);
        await _stateStore.SaveAsync(state, cancellationToken);
        Complete(repositoryPath, branch, sha, profile, result, false, cancellationToken);
        Log?.Invoke(Localization.Format(
            "Commit {0} reviewed, findings: {1}.",
            "Коммит {0} проверен, замечаний: {1}.",
            Short(sha), result.Findings.Count));
    }

    private async Task<ReviewResult> AnalyzeAndReportAsync(
        string repositoryPath,
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
        var diff = await _git.GetDiffAsync(repositoryPath, sha, cancellationToken);
        var result = new ReviewResult { EmptyDiff = string.IsNullOrWhiteSpace(diff) };
        if (!string.IsNullOrWhiteSpace(diff))
        {
            var chunks = DiffChunker.Split(diff);
            for (var index = 0; index < chunks.Count; index++)
            {
                Emit(ReviewStage.Chunk, profile, sha, index + 1, chunks.Count);
                var chunkHeader = chunks.Count == 1
                    ? string.Empty
                    : Localization.Format(
                        "Diff fragment {0} of {1}. Review this fragment independently.\n\n",
                        "Фрагмент diff {0} из {1}. Проверяй этот фрагмент независимо.\n\n",
                        index + 1, chunks.Count);
                var response = await _model.ReviewAsync(
                    profile, commit, chunkHeader + chunks[index], _configuration.LoadPrompt(), cancellationToken,
                    stage => Emit(stage, profile, sha, index + 1, chunks.Count));
                Emit(ReviewStage.Parsing, profile, sha, index + 1, chunks.Count);
                var chunkResult = ReviewParser.Parse(response);
                foreach (var finding in chunkResult.Findings)
                {
                    if (!result.Findings.Contains(finding))
                        result.Findings.Add(finding);
                }

                if (chunkResult.UnstructuredResponse is not null)
                {
                    var label = chunks.Count == 1
                        ? string.Empty
                        : Localization.Format("Fragment {0}:\n", "Фрагмент {0}:\n", index + 1);
                    result.UnstructuredResponse = string.Join(
                        Environment.NewLine + Environment.NewLine,
                        new[] { result.UnstructuredResponse, label + chunkResult.UnstructuredResponse }
                            .Where(value => !string.IsNullOrWhiteSpace(value)));
                }
            }
        }

        Emit(ReviewStage.Report, profile, sha);
        await _reportWriter.AppendAsync(
            repositoryPath, branch, commit, result, manualReview, cancellationToken);
        return result;
    }

    private static string Short(string sha) => sha[..Math.Min(8, sha.Length)];

    private void Emit(ReviewStage stage, ModelProfile profile, string sha, int chunk = 0, int total = 0) =>
        Publish(Progress, new ReviewProgress(stage, profile.Model, sha, chunk, total));

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
