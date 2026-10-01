using System.Diagnostics;
using GitReviewer.Models;
using GitReviewer.Services;
using Localization = GitReviewer.Services.Localization;

namespace GitReviewer;

public partial class MainWindow
{
    // Runtime identity is independent of the selected tab and survives project switching.
    private sealed class ProjectRuntime
    {
        public required ReviewRunner Runner { get; init; }
        public string Label = "";
        public string Identity = "";
        public string WorkTree = "";
        public string Branch = "";
        public string Status = "";
        public string Commit = "-", Subject = "", Details = "";
        public string? UsageKey;
        public int? Remaining;
        public DateTimeOffset? NextRun;
        public bool AutomaticStopPending;
        public readonly Stopwatch CommitClock = new(), AgentClock = new();
        public TimeSpan AgentTimeLimit = ModelClient.ReviewTimeLimit;
    }

    private readonly List<ProjectRuntime> _projectRuntimes = [];
    private ProjectRuntime _activeRuntime = null!;

    private void InitializeProjectRuntimes()
    {
        var catalog = _configuration.LoadProjects();
        foreach (var settings in catalog.Projects) _projectRuntimes.Add(CreateProjectRuntime(settings));
        _activeRuntime = _projectRuntimes[catalog.ActiveIndex];
    }

    private ProjectRuntime CreateProjectRuntime(AppSettings settings)
    {
        var git = new GitService();
        var runtime = new ProjectRuntime {
            Runner = new ReviewRunner(git, _model, _configuration, _stateStore, _reportWriter),
            Label = settings.RepositoryPath + " | " + settings.BranchRef,
            Status = Localization.Text("Stopped", "Остановлено")
        };
        var runner = runtime.Runner;
        runner.Log += message => AppendLog($"[{runtime.Label}] {message}");
        runner.ModelLog += message => _details.Append($"[{runtime.Label}] {message}");
        git.Diagnostic += message => _details.Append($"[{runtime.Label}] {message}");
        git.TransferProgress += message => {
            AppendLog($"[{runtime.Label}] Git fetch | {message}");
            Dispatch(() => runtime.Status = "Git fetch | " + message);
        };
        runner.StatusChanged += status => Dispatch(() => runtime.Status = status);
        runner.AutomaticStopped += () => Dispatch(() => runtime.AutomaticStopPending = true);
        runner.CommitChanged += commit => Dispatch(() => {
            runtime.Commit = commit;
            runtime.Subject = runtime.Details = "";
        });
        runner.CommitInfoChanged += commit => Dispatch(() => {
            runtime.Commit = commit.Sha;
            runtime.Subject = commit.Subject;
            runtime.Details = $"{commit.Author} · {commit.Date.LocalDateTime:g}";
        });
        runner.RemainingChanged += count => Dispatch(() => runtime.Remaining = count);
        runner.NextRunChanged += next => Dispatch(() => runtime.NextRun = next);
        runner.UsageReceived += item => {
            _tokenUsage.Record(item.Repository, item.Sha, item.Usage, DateTimeOffset.Now);
            Dispatch(() => runtime.UsageKey = TokenUsageStore.Key(item.Repository, item.Sha));
        };
        runner.Progress += progress => AppendProgress(runtime, progress);
        runner.Reviewed += reviewed => Dispatch(() => NotifyReviewed(reviewed));
        runner.Reviewed += _mail.Enqueue;
        return runtime;
    }

    private async Task ReserveProjectScopeAsync(AppSettings settings)
    {
        var identity = await _git.GetRepositoryIdentityAsync(settings.RepositoryPath, CancellationToken.None);
        var branch = await _git.ResolveBranchAsync(settings.RepositoryPath, settings.BranchRef, CancellationToken.None);
        if (_projectRuntimes.Any(p => p != _activeRuntime && p.Runner.IsRunning &&
            (string.Equals(p.WorkTree, identity.WorkTreeRoot, StringComparison.OrdinalIgnoreCase) ||
             (string.Equals(p.Identity, identity.CommonGitDirectory, StringComparison.OrdinalIgnoreCase) && p.Branch == branch))))
            throw new InvalidOperationException(Localization.Text(
                "This working copy or repository branch is already running in another project. Stop it first or use a different worktree and branch.",
                "Эта рабочая копия или ветка репозитория уже проверяется в другом проекте. Остановите его или выберите другой worktree и ветку."));
        _activeRuntime.Identity = identity.CommonGitDirectory;
        _activeRuntime.WorkTree = identity.WorkTreeRoot;
        _activeRuntime.Branch = branch;
        _activeRuntime.Label = identity.WorkTreeRoot + " | " + branch;
    }

    private void ShowRuntimeDashboard()
    {
        if (_activeRuntime is null) return;
        StatusRun.Text = _activeRuntime.Status;
        CommitRun.Text = _activeRuntime.Commit;
        CommitSubjectTextBlock.Text = _activeRuntime.Subject;
        CommitSubjectTextBlock.ToolTip = _activeRuntime.Subject;
        CommitDetailsText.Text = _activeRuntime.Details;
        for (var i = 0; i < _projectRuntimes.Count && i < ProjectButtons.Children.Count; i++)
            if (ProjectButtons.Children[i] is System.Windows.Controls.Button button)
                button.Content = (i + 1).ToString() + (_projectRuntimes[i].Runner.IsRunning ? " ▶" : "");
        if (_trayIcon is not null)
            _trayIcon.Text = TruncateTrayText(Localization.Format("Git Reviewer - running projects: {0}",
                "Git Reviewer - работают проектов: {0}", _projectRuntimes.Count(p => p.Runner.IsRunning)));
        CrashDiagnostics.Context = string.Join("\n", _projectRuntimes.Where(p => p.Runner.IsRunning)
            .Select(p => $"{p.Label}; commit: {p.Commit}"));
    }
}
