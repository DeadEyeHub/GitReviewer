using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using GitReviewer.Models;
using GitReviewer.Services;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Localization = GitReviewer.Services.Localization;

namespace GitReviewer;

public partial class MainWindow : Window
{
    private readonly ConfigurationStore _configuration = new();
    private readonly GitService _git = new();
    private readonly ModelClient _model = new();
    private readonly ReportWriter _reportWriter = new();
    private readonly ReviewRunner _runner;
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly Forms.ToolStripMenuItem _trayOpenItem;
    private readonly Forms.ToolStripMenuItem _trayStartItem;
    private readonly Forms.ToolStripMenuItem _trayStopItem;
    private readonly Forms.ToolStripMenuItem _trayOpenReportItem;
    private readonly Forms.ToolStripMenuItem _trayExitItem;
    private ModelsConfiguration _models;
    private CancellationTokenSource? _manualReviewCancellation;
    private Task? _manualReviewTask;
    private bool _loadingLanguage;
    private bool _loadingAuthentication;
    private bool _authenticationNeedsDetection;
    private bool _exitRequested;

    public MainWindow()
    {
        InitializeComponent();

        _runner = new ReviewRunner(
            _git, _model, _configuration, new StateStore(), _reportWriter);
        _runner.Log += message => Dispatch(() => AppendLog(message));
        _runner.StatusChanged += status => Dispatch(() => SetStatus(status));
        _runner.CommitChanged += commit => Dispatch(() => CommitRun.Text = commit);

        var settings = _configuration.LoadSettings();
        Localization.SetLanguage(settings.Language);
        _loadingLanguage = true;
        LanguageComboBox.ItemsSource = new[]
        {
            new LanguageOption("en", "English"),
            new LanguageOption("ru", "Русский")
        };
        LanguageComboBox.SelectedItem = LanguageComboBox.Items
            .Cast<LanguageOption>()
            .First(option => option.Code == Localization.Language);
        _loadingLanguage = false;
        _authenticationNeedsDetection = settings.GitAuthenticationMode == "auto";
        LoadAuthenticationOptions(_authenticationNeedsDetection
            ? "ssh-agent"
            : settings.GitAuthenticationMode);

        _models = _configuration.LoadModels();
        LoadSettings(settings);
        LoadProfiles();
        PromptTextBox.Text = _configuration.LoadPrompt();

        var trayMenu = new Forms.ContextMenuStrip();
        _trayOpenItem = new Forms.ToolStripMenuItem("Open Git Reviewer", null, (_, _) => Dispatch(ShowFromTray));
        trayMenu.Items.Add(_trayOpenItem);
        trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayStartItem = new Forms.ToolStripMenuItem("Start review", null,
            (_, _) => Dispatch(async () => await StartReviewAsync()));
        _trayStopItem = new Forms.ToolStripMenuItem("Stop review", null,
            (_, _) => Dispatch(async () => await StopReviewAsync()))
        {
            Enabled = false
        };
        trayMenu.Items.Add(_trayStartItem);
        trayMenu.Items.Add(_trayStopItem);
        _trayOpenReportItem = new Forms.ToolStripMenuItem("Open report", null,
            (_, _) => Dispatch(async () => await OpenReportAsync()));
        trayMenu.Items.Add(_trayOpenReportItem);
        trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayExitItem = new Forms.ToolStripMenuItem("Exit", null,
            (_, _) => Dispatch(async () => await ExitApplicationAsync()));
        trayMenu.Items.Add(_trayExitItem);

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Git Reviewer - stopped",
            Visible = true,
            ContextMenuStrip = trayMenu
        };
        _trayIcon.DoubleClick += (_, _) => Dispatch(ShowFromTray);
        ApplyLanguage();

        Closing += MainWindow_Closing;
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized)
                HideToTray();
        };
    }

    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    private void LoadSettings(AppSettings settings)
    {
        RepositoryPathTextBox.Text = settings.RepositoryPath;
        IntervalTextBox.Text = settings.PollIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        PullEnabledCheckBox.IsChecked = settings.PullEnabled;
        SshKeyPathTextBox.Text = settings.SshPrivateKeyPath;
        if (settings.RepositoryPath.Length > 0)
            _ = RefreshRepositoryInfoAsync();
    }

    private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingLanguage || LanguageComboBox.SelectedItem is not LanguageOption option)
            return;

        Localization.SetLanguage(option.Code);
        var settings = _configuration.LoadSettings();
        settings.Language = option.Code;
        _configuration.SaveSettings(settings);
        if (_configuration.SwitchDefaultPromptLanguage(option.Code, PromptTextBox.Text))
            PromptTextBox.Text = _configuration.LoadPrompt();
        ApplyLanguage();
    }

    private void ApplyLanguage()
    {
        Title = Localization.Text("Git Reviewer", "Проверка Git");
        ProjectTab.Header = Localization.Text("Project", "Проект");
        ModelsTab.Header = Localization.Text("Models", "Модели");
        PromptTab.Header = Localization.Text("System prompt", "Системный промпт");
        LogTab.Header = Localization.Text("Log", "Журнал");
        ProjectFolderLabel.Text = Localization.Text("Project folder", "Папка проекта");
        BrowseButton.Content = Localization.Text("Browse...", "Обзор...");
        IntervalLabel.Text = Localization.Text("Interval, seconds", "Интервал, секунд");
        ReceiveChangesLabel.Text = Localization.Text("Receive changes", "Получение изменений");
        PullEnabledCheckBox.Content = Localization.Text(
            "Run git pull --ff-only before each check",
            "Выполнять git pull --ff-only перед каждой проверкой");
        PullExplanationTextBlock.Text = Localization.Text(
            "Enable this for a clone that tracks a remote branch. It downloads new commits into the selected folder without creating merge commits. Disable it for a local-only repository.",
            "Включите для клона, который отслеживает удаленную ветку. Новые коммиты загружаются в выбранную папку без создания merge-коммитов. Отключите для полностью локального репозитория.");
        CurrentBranchLabel.Text = Localization.Text("Current branch", "Текущая ветка");
        LanguageLabel.Text = Localization.Text("Language", "Язык");
        SelectedCommitLabel.Text = Localization.Text("Review selected commit", "Проверить выбранный коммит");
        CommitShaTextBox.ToolTip = Localization.Text(
            "Enter a short or full commit SHA",
            "Введите короткий или полный SHA коммита");
        ReviewCommitButton.Content = Localization.Text("Review commit", "Проверить коммит");
        AuthenticationLabel.Text = Localization.Text("Authentication", "Аутентификация");
        SshKeyLabel.Text = Localization.Text("SSH private key", "Приватный SSH-ключ");
        BrowseSshKeyButton.Content = Localization.Text("Browse...", "Обзор...");
        RemoteAccessLabel.Text = Localization.Text("Remote access", "Доступ к remote");
        TestRemoteButton.Content = Localization.Text("Test repository access", "Проверить доступ");
        LoadAuthenticationOptions(GetAuthenticationMode());

        ActiveProfileLabel.Text = Localization.Text("Active profile", "Активный профиль");
        ProfileNameLabel.Text = Localization.Text("Profile name", "Название профиля");
        ModelLabel.Text = Localization.Text("Model", "Модель");
        ApiKeyEnvironmentLabel.Text = Localization.Text(
            "API key environment variable",
            "Переменная окружения с API-ключом");
        AddProfileButton.Content = Localization.Text("Add", "Добавить");
        SaveProfileButton.Content = Localization.Text("Save", "Сохранить");
        DeleteProfileButton.Content = Localization.Text("Delete", "Удалить");
        TestModelButton.Content = Localization.Text("Test connection", "Проверить подключение");
        SavePromptButton.Content = Localization.Text("Save", "Сохранить");
        ReloadPromptButton.Content = Localization.Text("Reload", "Перечитать");
        RestorePromptButton.Content = Localization.Text("Restore default", "Вернуть стандартный");

        StatusLabelRun.Text = Localization.Text("Status: ", "Статус: ");
        CurrentCommitLabelRun.Text = Localization.Text("Current commit: ", "Текущий коммит: ");
        StartButton.Content = Localization.Text("Start", "Старт");
        StopButton.Content = Localization.Text("Stop", "Стоп");
        OpenReportButton.Content = Localization.Text("Open report", "Открыть отчет");
        HideToTrayButton.Content = Localization.Text("Hide to tray", "Скрыть в трей");

        _trayOpenItem.Text = Localization.Text("Open Git Reviewer", "Открыть Git Reviewer");
        _trayStartItem.Text = Localization.Text("Start review", "Запустить проверку");
        _trayStopItem.Text = Localization.Text("Stop review", "Остановить проверку");
        _trayOpenReportItem.Text = Localization.Text("Open report", "Открыть отчет");
        _trayExitItem.Text = Localization.Text("Exit", "Выход");
        SetStatus(_runner.IsRunning
            ? Localization.Text("Running", "Работает")
            : Localization.Text("Stopped", "Остановлено"));
    }

    private void LoadAuthenticationOptions(string selectedMode)
    {
        _loadingAuthentication = true;
        var options = new[]
        {
            new AuthenticationOption("ssh-agent", "SSH Agent"),
            new AuthenticationOption(
                "ssh-key",
                Localization.Text("SSH Key File", "SSH-ключ из файла")),
            new AuthenticationOption("https", "HTTPS")
        };
        AuthenticationComboBox.ItemsSource = options;
        AuthenticationComboBox.SelectedItem = options.FirstOrDefault(option => option.Code == selectedMode)
            ?? options[0];
        _loadingAuthentication = false;
        UpdateAuthenticationControls();
    }

    private void AuthenticationComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingAuthentication)
        {
            _authenticationNeedsDetection = false;
            UpdateAuthenticationControls();
            SaveAuthenticationPreferences();
        }
    }

    private void UpdateAuthenticationControls()
    {
        var mode = GetAuthenticationMode();
        var usesKeyFile = mode == "ssh-key";
        SshKeyPathTextBox.IsEnabled = usesKeyFile;
        BrowseSshKeyButton.IsEnabled = usesKeyFile;
        AuthenticationHelpTextBlock.Text = mode switch
        {
            "ssh-key" => Localization.Text(
                "Select a private key file. Add its public key to the Git server. Use SSH Agent if the key has a passphrase.",
                "Выберите файл приватного ключа. Добавьте публичный ключ на Git-сервер. Для ключа с паролем используйте SSH Agent."),
            "https" => Localization.Text(
                "Uses credentials already stored by Git Credential Manager. The origin URL must start with https://.",
                "Используются учетные данные из Git Credential Manager. Адрес origin должен начинаться с https://."),
            _ => Localization.Text(
                "Uses keys loaded into Windows OpenSSH Agent. The origin must use an SSH URL.",
                "Используются ключи, загруженные в Windows OpenSSH Agent. Для origin нужен SSH-адрес.")
        };
    }

    private string GetAuthenticationMode() =>
        (AuthenticationComboBox.SelectedItem as AuthenticationOption)?.Code ?? "ssh-agent";

    private void BrowseSshKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Localization.Text("Select an SSH private key", "Выберите приватный SSH-ключ"),
            Filter = Localization.Text(
                "SSH private keys|id_*;*.pem;*.key|All files|*.*",
                "Приватные SSH-ключи|id_*;*.pem;*.key|Все файлы|*.*")
        };
        if (dialog.ShowDialog(this) == true)
        {
            SshKeyPathTextBox.Text = dialog.FileName;
            SaveAuthenticationPreferences();
        }
    }

    private void SshKeyPathTextBox_LostKeyboardFocus(
        object sender,
        System.Windows.Input.KeyboardFocusChangedEventArgs e) =>
        SaveAuthenticationPreferences();

    private void SaveAuthenticationPreferences()
    {
        if (_loadingAuthentication)
            return;
        var settings = _configuration.LoadSettings();
        settings.GitAuthenticationMode = GetAuthenticationMode();
        settings.SshPrivateKeyPath = SshKeyPathTextBox.Text.Trim();
        _configuration.SaveSettings(settings);
    }

    private async void TestRemote_Click(object sender, RoutedEventArgs e)
    {
        TestRemoteButton.IsEnabled = false;
        RemoteAccessStatusTextBlock.Text = Localization.Text(
            "Testing repository access...",
            "Проверяется доступ к репозиторию...");
        try
        {
            var settings = ReadSettingsFromForm();
            _configuration.SaveSettings(settings);
            await _git.TestRemoteAccessAsync(
                settings.RepositoryPath, settings, CancellationToken.None);
            RemoteAccessStatusTextBlock.Text = Localization.Text(
                "Repository access confirmed.",
                "Доступ к репозиторию подтвержден.");
        }
        catch (Exception exception)
        {
            RemoteAccessStatusTextBlock.Text = Localization.Format(
                "Repository access failed: {0}",
                "Ошибка доступа к репозиторию: {0}",
                exception.Message);
        }
        finally
        {
            TestRemoteButton.IsEnabled = true;
        }
    }

    private void LoadProfiles(string? selectName = null)
    {
        ProfilesComboBox.ItemsSource = null;
        ProfilesComboBox.ItemsSource = _models.Profiles.Select(profile => profile.Name).ToList();
        var name = selectName ?? _models.ActiveProfile;
        ProfilesComboBox.SelectedItem = _models.Profiles
            .FirstOrDefault(profile => profile.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Name;
        if (ProfilesComboBox.SelectedItem is null && _models.Profiles.Count > 0)
            ProfilesComboBox.SelectedIndex = 0;
    }

    private async void BrowseRepository_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = Localization.Text("Select a Git repository", "Выберите Git-репозиторий")
        };
        if (!string.IsNullOrWhiteSpace(RepositoryPathTextBox.Text) && Directory.Exists(RepositoryPathTextBox.Text))
            dialog.InitialDirectory = RepositoryPathTextBox.Text;
        if (dialog.ShowDialog(this) == true)
        {
            RepositoryPathTextBox.Text = dialog.FolderName;
            await RefreshRepositoryInfoAsync();
        }
    }

    private async Task RefreshRepositoryInfoAsync()
    {
        try
        {
            var path = Path.GetFullPath(RepositoryPathTextBox.Text.Trim());
            await _git.ValidateRepositoryAsync(path, CancellationToken.None);
            BranchTextBlock.Text = await _git.GetBranchAsync(path, CancellationToken.None);
            var remote = await _git.GetRemoteSummaryAsync(path, CancellationToken.None);
            RemoteTextBlock.Text = remote.Length == 0
                ? Localization.Text("Not configured", "Не настроен")
                : remote;
            if (_authenticationNeedsDetection)
            {
                var detectedMode = await _git.DetectAuthenticationModeAsync(path, CancellationToken.None);
                if (_authenticationNeedsDetection)
                {
                    LoadAuthenticationOptions(detectedMode);
                    _authenticationNeedsDetection = false;
                }
            }
        }
        catch (Exception exception)
        {
            BranchTextBlock.Text = "-";
            RemoteTextBlock.Text = exception.Message;
        }
    }

    private void ProfilesComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfilesComboBox.SelectedItem is not string name)
            return;
        var profile = _models.Profiles.FirstOrDefault(item => item.Name == name);
        if (profile is null)
            return;
        ProfileNameTextBox.Text = profile.Name;
        EndpointTextBox.Text = profile.Endpoint;
        ModelNameTextBox.Text = profile.Model;
        ApiKeyPasswordBox.Password = profile.ApiKey;
        ApiKeyEnvironmentTextBox.Text = profile.ApiKeyEnvironment;
        _models.ActiveProfile = profile.Name;
    }

    private void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        ProfilesComboBox.SelectedItem = null;
        ProfileNameTextBox.Clear();
        EndpointTextBox.Clear();
        ModelNameTextBox.Clear();
        ApiKeyPasswordBox.Clear();
        ApiKeyEnvironmentTextBox.Clear();
        ProfileNameTextBox.Focus();
    }

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var profile = ReadProfileFromForm();
            var existing = _models.Profiles.FirstOrDefault(item =>
                item.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                _models.Profiles.Add(profile);
            }
            else
            {
                existing.Endpoint = profile.Endpoint;
                existing.Model = profile.Model;
                existing.ApiKey = profile.ApiKey;
                existing.ApiKeyEnvironment = profile.ApiKeyEnvironment;
                profile = existing;
            }

            _models.ActiveProfile = profile.Name;
            _configuration.SaveModels(_models);
            LoadProfiles(profile.Name);
            ModelStatusTextBlock.Text = Localization.Text(
                "Profile saved locally.",
                "Профиль сохранен локально.");
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesComboBox.SelectedItem is not string name)
            return;
        if (System.Windows.MessageBox.Show(Localization.Format(
                "Delete profile '{0}'?",
                "Удалить профиль '{0}'?",
                name), "Git Reviewer",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _models.Profiles.RemoveAll(profile => profile.Name == name);
        _models.ActiveProfile = _models.Profiles.FirstOrDefault()?.Name ?? string.Empty;
        _configuration.SaveModels(_models);
        LoadProfiles();
        if (_models.Profiles.Count == 0)
            AddProfile_Click(sender, e);
    }

    private async void TestModel_Click(object sender, RoutedEventArgs e)
    {
        TestModelButton.IsEnabled = false;
        ModelStatusTextBlock.Text = Localization.Text(
            "Testing connection...",
            "Проверяется подключение...");
        try
        {
            ModelStatusTextBlock.Text = await _model.TestConnectionAsync(
                ReadProfileFromForm(), CancellationToken.None);
        }
        catch (Exception exception)
        {
            ModelStatusTextBlock.Text = Localization.Format(
                "Connection error: {0}",
                "Ошибка подключения: {0}",
                exception.Message);
        }
        finally
        {
            TestModelButton.IsEnabled = true;
        }
    }

    private ModelProfile ReadProfileFromForm()
    {
        var name = ProfileNameTextBox.Text.Trim();
        if (name.Length == 0)
            throw new InvalidOperationException(Localization.Text(
                "Enter a profile name.",
                "Укажите название профиля."));
        if (name.IndexOfAny(['[', ']', '\r', '\n', '=']) >= 0)
            throw new InvalidOperationException(Localization.Text(
                "The profile name contains invalid characters.",
                "Название профиля содержит недопустимые символы."));

        return new ModelProfile
        {
            Name = name,
            Endpoint = EndpointTextBox.Text.Trim(),
            Model = ModelNameTextBox.Text.Trim(),
            ApiKey = ApiKeyPasswordBox.Password.Trim(),
            ApiKeyEnvironment = ApiKeyEnvironmentTextBox.Text.Trim()
        };
    }

    private void SavePrompt_Click(object sender, RoutedEventArgs e)
    {
        _configuration.SavePrompt(PromptTextBox.Text);
        AppendLog(Localization.Text("System prompt saved.", "Системный промпт сохранен."));
    }

    private void ReloadPrompt_Click(object sender, RoutedEventArgs e) =>
        PromptTextBox.Text = _configuration.LoadPrompt();

    private void RestorePrompt_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show(Localization.Text(
                "Restore the default system prompt?",
                "Вернуть стандартный системный промпт?"), "Git Reviewer",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _configuration.RestoreDefaultPrompt(Localization.Language);
        PromptTextBox.Text = _configuration.LoadPrompt();
        AppendLog(Localization.Text(
            "Default system prompt restored.",
            "Стандартный системный промпт восстановлен."));
    }

    private async void ReviewCommit_Click(object sender, RoutedEventArgs e)
    {
        if (_manualReviewTask is { IsCompleted: false })
            return;

        try
        {
            var settings = ReadSettingsFromForm();
            var profile = ReadProfileFromForm();
            var revision = CommitShaTextBox.Text.Trim();
            _configuration.SavePrompt(PromptTextBox.Text);
            ReviewCommitButton.IsEnabled = false;
            StartButton.IsEnabled = false;
            _trayStartItem.Enabled = false;
            _manualReviewCancellation = new CancellationTokenSource();
            _manualReviewTask = _runner.ReviewSingleCommitAsync(
                settings, profile, revision, _manualReviewCancellation.Token);
            await _manualReviewTask;
        }
        catch (OperationCanceledException)
        {
            AppendLog(Localization.Text("Manual review canceled.", "Ручная проверка отменена."));
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
        finally
        {
            _manualReviewCancellation?.Dispose();
            _manualReviewCancellation = null;
            _manualReviewTask = null;
            ReviewCommitButton.IsEnabled = !_runner.IsRunning;
            StartButton.IsEnabled = !_runner.IsRunning;
            _trayStartItem.Enabled = !_runner.IsRunning;
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e) => await StartReviewAsync();

    private async Task StartReviewAsync()
    {
        if (_runner.IsRunning || _manualReviewTask is { IsCompleted: false })
            return;
        StartButton.IsEnabled = false;
        _trayStartItem.Enabled = false;
        ReviewCommitButton.IsEnabled = false;
        try
        {
            var settings = ReadSettingsFromForm();
            var profile = ReadProfileFromForm();
            _configuration.SaveSettings(settings);
            _configuration.SavePrompt(PromptTextBox.Text);

            var existing = _models.Profiles.FirstOrDefault(item =>
                item.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
                _models.Profiles.Add(profile);
            else
            {
                existing.Endpoint = profile.Endpoint;
                existing.Model = profile.Model;
                existing.ApiKey = profile.ApiKey;
                existing.ApiKeyEnvironment = profile.ApiKeyEnvironment;
                profile = existing;
            }
            _models.ActiveProfile = profile.Name;
            _configuration.SaveModels(_models);

            await _git.ValidateRepositoryAsync(Path.GetFullPath(settings.RepositoryPath), CancellationToken.None);
            await RefreshRepositoryInfoAsync();
            if (!_runner.Start(settings, profile))
                throw new InvalidOperationException(Localization.Text(
                    "Another review operation is already running.",
                    "Уже выполняется другая операция проверки."));
            SetRunningControls(true);
            SetStatus(Localization.Text("Starting", "Запускается"));
        }
        catch (Exception exception)
        {
            SetRunningControls(_runner.IsRunning);
            ShowError(exception.Message);
        }
    }

    private AppSettings ReadSettingsFromForm()
    {
        var path = RepositoryPathTextBox.Text.Trim();
        if (path.Length == 0)
            throw new InvalidOperationException(Localization.Text(
                "Select a Git repository folder.",
                "Выберите папку Git-репозитория."));
        if (!int.TryParse(IntervalTextBox.Text, out var seconds) || seconds is < 5 or > 86_400)
            throw new InvalidOperationException(Localization.Text(
                "The interval must be between 5 and 86400 seconds.",
                "Интервал должен быть от 5 до 86400 секунд."));
        return new AppSettings
        {
            RepositoryPath = Path.GetFullPath(path),
            PollIntervalSeconds = seconds,
            PullEnabled = PullEnabledCheckBox.IsChecked == true,
            Language = Localization.Language,
            GitAuthenticationMode = GetAuthenticationMode(),
            SshPrivateKeyPath = SshKeyPathTextBox.Text.Trim()
        };
    }

    private async void Stop_Click(object sender, RoutedEventArgs e) => await StopReviewAsync();

    private async Task StopReviewAsync()
    {
        await _runner.StopAsync();
        SetRunningControls(false);
    }

    private async void OpenReport_Click(object sender, RoutedEventArgs e) => await OpenReportAsync();

    private async Task OpenReportAsync()
    {
        try
        {
            var path = Path.GetFullPath(RepositoryPathTextBox.Text.Trim());
            var branch = await _git.GetBranchAsync(path, CancellationToken.None);
            _reportWriter.Open(path, branch);
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private void HideToTray_Click(object sender, RoutedEventArgs e) => HideToTray();

    private void HideToTray()
    {
        Hide();
        _trayIcon.Text = _runner.IsRunning
            ? Localization.Text("Git Reviewer - running in background", "Git Reviewer - работает в фоне")
            : Localization.Text("Git Reviewer - stopped", "Git Reviewer - остановлен");
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_exitRequested)
            return;
        e.Cancel = true;
        HideToTray();
    }

    private async Task ExitApplicationAsync()
    {
        _exitRequested = true;
        _manualReviewCancellation?.Cancel();
        if (_manualReviewTask is not null)
        {
            try
            {
                await _manualReviewTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                AppendLog(Localization.Format(
                    "Manual review stopped with an error: {0}",
                    "Ручная проверка остановлена с ошибкой: {0}",
                    exception.Message));
            }
        }
        await _runner.StopAsync();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    private void SetRunningControls(bool running)
    {
        StartButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        _trayStartItem.Enabled = !running;
        _trayStopItem.Enabled = running;
        ReviewCommitButton.IsEnabled = !running && _manualReviewTask is not { IsCompleted: false };
    }

    private void SetStatus(string status)
    {
        StatusRun.Text = status;
        _trayIcon.Text = TruncateTrayText($"Git Reviewer - {status.ToLowerInvariant()}");
    }

    private void AppendLog(string message)
    {
        LogTextBox.AppendText($"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}");
        LogTextBox.ScrollToEnd();
    }

    private void Dispatch(Action action)
    {
        if (Dispatcher.CheckAccess())
            action();
        else
            Dispatcher.Invoke(action);
    }

    private static string TruncateTrayText(string value) => value[..Math.Min(value.Length, 63)];

    private static void ShowError(string message) => System.Windows.MessageBox.Show(
        message, "Git Reviewer", MessageBoxButton.OK, MessageBoxImage.Error);

    private sealed record LanguageOption(string Code, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record AuthenticationOption(string Code, string Label)
    {
        public override string ToString() => Label;
    }
}
