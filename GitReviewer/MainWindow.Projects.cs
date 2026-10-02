using System.Globalization;
using System.Windows;
using GitReviewer.Models;
using Controls = System.Windows.Controls;
using Localization = GitReviewer.Services.Localization;

namespace GitReviewer;

public partial class MainWindow
{
    private bool _switchingProject;

    private string ProjectCaption(int index) => string.IsNullOrWhiteSpace(_projectRuntimes[index].DisplayName)
        ? (index + 1).ToString(CultureInfo.InvariantCulture) : _projectRuntimes[index].DisplayName;

    private void RenameProject(string name)
    {
        var catalog = _configuration.LoadProjects();
        catalog.Projects[catalog.ActiveIndex].DisplayName = name;
        _configuration.SaveProjects(catalog);
        _activeRuntime.DisplayName = catalog.Projects[catalog.ActiveIndex].DisplayName;
        RenderProjectButtons();
    }

    private void RenameProject_Click(object sender, RoutedEventArgs e)
    {
        if (_switchingProject || _exitRequested) return;
        var dialog = new Window {
            Owner = this, Title = Localization.Text("Project name", "Название проекта"),
            Width = 430, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false
        };
        var panel = new Controls.StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new Controls.TextBlock {
            Text = Localization.Text("Leave empty to display the project number. Maximum 80 characters.",
                "Оставьте пустым, чтобы показывать номер. Максимум 80 символов."),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
        });
        var input = new Controls.TextBox { Text = _activeRuntime.DisplayName, MaxLength = 80 };
        panel.Children.Add(input);
        var buttons = new Controls.StackPanel { Orientation = Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var save = new Controls.Button { Content = Localization.Text("Save", "Сохранить"), IsDefault = true, Padding = new Thickness(12, 5, 12, 5) };
        var cancel = new Controls.Button { Content = Localization.Text("Cancel", "Отмена"), IsCancel = true,
            Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(8, 0, 0, 0) };
        save.Click += (_, _) => {
            try { RenameProject(input.Text); dialog.DialogResult = true; }
            catch (Exception exception) { ShowError(exception.Message); }
        };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        dialog.ShowDialog();
    }

    private void RenderProjectButtons()
    {
        var catalog = _configuration.LoadProjects();
        ProjectsLabel.Text = Localization.Text("Projects:", "Проекты:");
        RenameProjectButton.Content = Localization.Text("Rename…", "Переименовать…");
        RenameProjectButton.IsEnabled = !_switchingProject;
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
                Content = ProjectCaption(index), ToolTip = description,
                MinWidth = 36, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 6, 0),
                FontWeight = index == catalog.ActiveIndex ? FontWeights.Bold : FontWeights.Normal,
                IsEnabled = !_switchingProject && index != catalog.ActiveIndex
            };
            System.Windows.Automation.AutomationProperties.SetName(button, $"{ProjectCaption(index)}: {description}");
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
            FetchSubmodules = FetchSubmodulesCheckBox.IsChecked == true,
            UpdateSubmodulesAfterAdvance = UpdateSubmodulesCheckBox.IsChecked == true,
            QuietHoursEnabled = QuietHoursCheckBox.IsChecked == true,
            QuietHoursStart = QuietStartTextBox.Text.Trim(), QuietHoursEnd = QuietEndTextBox.Text.Trim(),
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
            SaveProjectDraft();
            _switchingProject = true;
            MainTabs.IsEnabled = ProjectButtons.IsEnabled = AddProjectButton.IsEnabled = RemoveProjectButton.IsEnabled = false;
            // Removing a project stops only its own worker; ordinary switching never stops it.
            if (target == -2) await StopReviewAsync();
            if (_exitRequested) return;
            ++_repositoryVersion;
            ++_commitListVersion;
            catalog = _configuration.LoadProjects();
            if (target == -1)
            {
                catalog.Projects.Add(new AppSettings { Language = Localization.Language, ProjectId = Guid.NewGuid().ToString("N") });
                _projectRuntimes.Add(CreateProjectRuntime(catalog.Projects[^1]));
                catalog.ActiveIndex = catalog.Projects.Count - 1;
            }
            else if (target == -2)
            {
                catalog.Projects.RemoveAt(catalog.ActiveIndex);
                _projectRuntimes.RemoveAt(catalog.ActiveIndex);
                catalog.ActiveIndex = Math.Min(catalog.ActiveIndex, catalog.Projects.Count - 1);
            }
            else catalog.ActiveIndex = target;
            _configuration.SaveProjects(catalog);
            _activeRuntime = _projectRuntimes[catalog.ActiveIndex];
            RefreshLogs(true);
            var settings = catalog.Projects[catalog.ActiveIndex];
            _repositoryReady = false;
            _repositoryPath = _repositoryCommonGitDirectory = _testedRepositoryIdentity = string.Empty;
            _repositoryWasKnown = false;
            _loadingRepository = true;
            BranchComboBox.ItemsSource = Array.Empty<string>();
            CommitShaTextBox.Text = string.Empty;

            RemoteTextBlock.Text = RemoteAccessStatusTextBlock.Text = string.Empty;
            _authenticationNeedsDetection = settings.GitAuthenticationMode == "auto";
            LoadAuthenticationOptions(_authenticationNeedsDetection ? "ssh-agent" : settings.GitAuthenticationMode);
            CommitRun.Text = "-";
            CommitSubjectTextBlock.Text = CommitDetailsText.Text = string.Empty;
            CommitSubjectTextBlock.ToolTip = null;
            LoadSettings(settings);
            ShowRuntimeDashboard();
            SetRunningControls(_runner.IsRunning);
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
