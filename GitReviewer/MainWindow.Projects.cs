using System.Globalization;
using System.Windows;
using GitReviewer.Models;
using Controls = System.Windows.Controls;
using Localization = GitReviewer.Services.Localization;

namespace GitReviewer;

public partial class MainWindow
{
    private bool _switchingProject;

    private void RenderProjectButtons()
    {
        var catalog = _configuration.LoadProjects();
        ProjectsLabel.Text = Localization.Text("Projects:", "Проекты:");
        AddProjectButton.ToolTip = Localization.Text("Add project", "Добавить проект");
        RemoveProjectButton.ToolTip = Localization.Text("Remove project from list; keep files and reports", "Убрать проект из списка; сохранить файлы и отчёты");
        RemoveProjectButton.IsEnabled = !_switchingProject && catalog.Projects.Count > 1;
        ProjectButtons.Children.Clear();
        for (var index = 0; index < catalog.Projects.Count; index++)
        {
            var number = index;
            var project = catalog.Projects[index];
            var description = string.IsNullOrWhiteSpace(project.RepositoryPath)
                ? Localization.Text("New project", "Новый проект") : project.RepositoryPath + "\n" + project.BranchRef;
            var button = new Controls.Button {
                Content = (index + 1).ToString(CultureInfo.InvariantCulture), ToolTip = description,
                MinWidth = 36, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 6, 0),
                FontWeight = index == catalog.ActiveIndex ? FontWeights.Bold : FontWeights.Normal,
                IsEnabled = !_switchingProject && index != catalog.ActiveIndex
            };
            System.Windows.Automation.AutomationProperties.SetName(button, $"{index + 1}: {description}");
            button.Click += async (_, _) => await ChangeProjectAsync(number);
            ProjectButtons.Children.Add(button);
        }
    }

    private void SaveProjectDraft()
    {
        if (!int.TryParse(IntervalTextBox.Text, out var interval) || interval is < 5 or > 86400)
            throw new InvalidOperationException(Localization.Text("The interval must be between 5 and 86400 seconds.", "Интервал должен быть от 5 до 86400 секунд."));
        _configuration.SaveSettings(new AppSettings {
            RepositoryPath = RepositoryPathTextBox.Text.Trim(), BranchRef = _selectedBranch,
            PollIntervalSeconds = interval, PullEnabled = PullEnabledCheckBox.IsChecked == true,
            Language = Localization.Language, GitAuthenticationMode = _authenticationNeedsDetection ? "auto" : GetAuthenticationMode(),
            SshPrivateKeyPath = SshKeyPathTextBox.Text.Trim(), PlinkPath = PlinkPathTextBox.Text.Trim()
        });
    }

    private async void AddProject_Click(object sender, RoutedEventArgs e) => await ChangeProjectAsync(-1);
    private async void RemoveProject_Click(object sender, RoutedEventArgs e) => await ChangeProjectAsync(-2);

    private async Task ChangeProjectAsync(int target)
    {
        if (_switchingProject || _exitRequested || _startingReview || _settingStartCommit) return;
        if (_manualReviewTask is { IsCompleted: false })
        {
            ShowError(Localization.Text("Wait for the manual review to finish before switching projects.", "Дождитесь завершения ручной проверки перед переключением проекта."));
            return;
        }
        try
        {
            var catalog = _configuration.LoadProjects();
            if (target == catalog.ActiveIndex || target >= catalog.Projects.Count) return;
            if (target == -2 && (catalog.Projects.Count == 1 || System.Windows.MessageBox.Show(
                Localization.Text("Remove this project from the list? Repository files, reports and review progress will be kept.", "Убрать проект из списка? Файлы репозитория, отчёты и позиция проверки сохранятся."),
                "GitReviewer", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)) return;
            if (_runner.IsRunning && System.Windows.MessageBox.Show(
                Localization.Text("Stop the current review and switch projects? Start the new project manually when ready.", "Остановить текущую проверку и переключить проект? Новый проект запускается кнопкой «Старт»."),
                "GitReviewer", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            SaveProjectDraft();
            _switchingProject = true;
            MainTabs.IsEnabled = ProjectButtons.IsEnabled = AddProjectButton.IsEnabled = RemoveProjectButton.IsEnabled = false;
            await StopReviewAsync();
            // Drain queued events from the old review before resetting the dashboard.
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            if (_exitRequested) return;
            ++_repositoryVersion;
            ++_commitListVersion;
            catalog = _configuration.LoadProjects();
            if (target == -1)
            {
                catalog.Projects.Add(new AppSettings { Language = Localization.Language });
                catalog.ActiveIndex = catalog.Projects.Count - 1;
            }
            else if (target == -2)
            {
                catalog.Projects.RemoveAt(catalog.ActiveIndex);
                catalog.ActiveIndex = Math.Min(catalog.ActiveIndex, catalog.Projects.Count - 1);
            }
            else catalog.ActiveIndex = target;
            _configuration.SaveProjects(catalog);
            var settings = catalog.Projects[catalog.ActiveIndex];
            _repositoryReady = false;
            _repositoryPath = _repositoryCommonGitDirectory = _testedRepositoryIdentity = string.Empty;
            _repositoryWasKnown = _automaticStopPending = false;
            _loadingRepository = true;
            BranchComboBox.ItemsSource = Array.Empty<string>();
            CommitShaTextBox.Text = string.Empty;
            CommitShaTextBox.ItemsSource = null;
            RemoteTextBlock.Text = RemoteAccessStatusTextBlock.Text = string.Empty;
            _authenticationNeedsDetection = settings.GitAuthenticationMode == "auto";
            LoadAuthenticationOptions(_authenticationNeedsDetection ? "ssh-agent" : settings.GitAuthenticationMode);
            CommitRun.Text = "-";
            CommitSubjectTextBlock.Text = CommitDetailsText.Text = string.Empty;
            CommitSubjectTextBlock.ToolTip = null;
            _usageCommitKey = null;
            _remainingCommits = null;
            _nextAutomaticRun = null;
            _commitClock.Reset();
            _agentClock.Reset();
            LoadSettings(settings);
            SetStatus(Localization.Text("Stopped", "Остановлено"));
            UpdateStartCommitButton();
        }
        catch (Exception exception) { ShowError(exception.Message); }
        finally
        {
            _switchingProject = false;
            MainTabs.IsEnabled = ProjectButtons.IsEnabled = AddProjectButton.IsEnabled = true;
            try { RenderProjectButtons(); }
            catch (Exception exception) { ShowError(exception.Message); }
        }
    }
}
