using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Reflection;
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
    private static readonly string AppVersion =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";

    private readonly ConfigurationStore _configuration = new();
    private readonly GitService _git = new();
    private readonly ModelClient _model = new();
    private readonly ReportWriter _reportWriter = new();
    private readonly StateStore _stateStore = new();
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
    private int _modelRequestVersion;
    private IReadOnlyList<ModelClient.AvailableModel> _availableModels = [];
    private string _selectedBranch = string.Empty;
    private bool _loadingRepository = true;
    private bool _repositoryReady;
    private string _repositoryPath = string.Empty;
    private string _repositoryCommonGitDirectory = string.Empty;
    private bool _repositoryWasKnown;
    private readonly HashSet<string> _repositoriesAwaitingTest = new(StringComparer.OrdinalIgnoreCase);
    private string _testedRepositoryIdentity = string.Empty;
    private bool _settingStartCommit;
    private int _commitListVersion;
    private int _repositoryVersion;
    private readonly PersistentLog _journal = new(AppPaths.JournalLog);
    private readonly PersistentLog _details = new(
        AppPaths.ModelLog, maxBytes: 32 * 1024 * 1024, maxTailLines: 10_000, maxTailCharacters: 4_000_000);
    private readonly System.Windows.Threading.DispatcherTimer _logTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private LogWindow? _logWindow;

    public MainWindow()
    {
        InitializeComponent();

        _runner = new ReviewRunner(
            _git, _model, _configuration, _stateStore, _reportWriter);
        _runner.Log += AppendLog;
        _runner.ModelLog += _details.Append;
        _git.Diagnostic += _details.Append;
        _runner.StatusChanged += status => Dispatch(() => SetStatus(status));
        _runner.CommitChanged += commit => Dispatch(() => CommitRun.Text = commit);
        _runner.Progress += AppendProgress;
        _runner.Reviewed += reviewed => Dispatch(() => NotifyReviewed(reviewed));
        _logTimer.Tick += (_, _) => RefreshLogs();
        _logTimer.Start();
        RefreshLogs();

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
        if (_exitRequested) return;
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    private void LoadSettings(AppSettings settings)
    {
        _selectedBranch = settings.BranchRef;
        RepositoryPathTextBox.Text = settings.RepositoryPath;
        IntervalTextBox.Text = settings.PollIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        PullEnabledCheckBox.IsChecked = settings.PullEnabled;
        SshKeyPathTextBox.Text = settings.SshPrivateKeyPath;
        PlinkPathTextBox.Text = settings.PlinkPath;
        _loadingRepository = false;
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
        Title = Localization.Format(
            "Git Reviewer {0}",
            "Проверка Git {0}",
            AppVersion);
        ProjectTab.Header = Localization.Text("Project", "Проект");
        ModelsTab.Header = Localization.Text("Models", "Модели");
        PromptTab.Header = Localization.Text("System prompt", "Системный промпт");
        LogTab.Header = Localization.Text("Journal", "Журнал");
        OpenLogButton.Content = Localization.Text("Log", "Лог");
        RefreshBranchesButton.Content = Localization.Text("Refresh", "Обновить");
        if (_logWindow is not null) _logWindow.Title = Localization.Text("Log", "Лог");
        ProjectFolderLabel.Text = Localization.Text("Project folder", "Папка проекта");
        BrowseButton.Content = Localization.Text("Browse...", "Обзор...");
        IntervalLabel.Text = Localization.Text("Interval, seconds", "Интервал, секунд");
        ReceiveChangesLabel.Text = Localization.Text("Receive changes", "Получение изменений");
        PullEnabledCheckBox.Content = Localization.Text(
            "Run git fetch before each check",
            "Выполнять git fetch перед каждой проверкой");
        PullExplanationTextBlock.Text = Localization.Text(
            "Fetch updates remote-tracking refs only, never the checkout. Select refs/remotes/... to review remote updates; local branches are read as-is. It is skipped when no remote is configured.",
            "Fetch обновляет удаленные ссылки, не рабочие файлы. Для удаленных обновлений выберите refs/remotes/...; локальные ветки читаются как есть. Если remote не настроен, fetch пропускается.");
        CurrentBranchLabel.Text = Localization.Text("Selected branch", "Выбранная ветка");
        LanguageLabel.Text = Localization.Text("Language", "Язык");
        SelectedCommitLabel.Text = Localization.Text("Selected commit", "Выбранный коммит");
        CommitShaTextBox.ToolTip = Localization.Text(
            "Select from the latest 100 commits of the selected branch, or enter a short/full SHA. The list refreshes when opened.",
            "Выберите из последних 100 коммитов выбранной ветки или введите короткий/полный SHA. Список обновляется при открытии.");
        ReviewCommitButton.Content = Localization.Text("Review commit", "Проверить коммит");
        SetStartCommitButton.Content = Localization.Text(
            "Start from selected commit",
            "Начать с выбранного коммита");
        SetStartCommitButton.ToolTip = Localization.Text(
            "Use the SHA entered above as the first commit of the next automatic review.",
            "Использовать введенный выше SHA как первый коммит следующей автоматической проверки.");
        AuthenticationLabel.Text = Localization.Text("Authentication", "Аутентификация");
        SshKeyLabel.Text = Localization.Text("SSH private key", "Приватный SSH-ключ");
        BrowseSshKeyButton.Content = Localization.Text("Browse...", "Обзор...");
        PlinkPathLabel.Text = Localization.Text("Plink executable", "Исполняемый файл Plink");
        BrowsePlinkButton.Content = Localization.Text("Browse...", "Обзор...");
        PlinkPathTextBox.ToolTip = Localization.Text(
            "Path to plink.exe. Leave empty to detect PuTTY in its standard locations or PATH.",
            "Путь к plink.exe. Оставьте пустым для поиска PuTTY в стандартных папках или PATH.");
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
        LoadModelsButton.Content = Localization.Text("Load models", "Загрузить модели");
        EndpointHelpTextBlock.Text = Localization.Text(
            "Use a /v1 base URL, /v1/models, or a full /chat/completions URL. API key is optional.",
            "Укажите базовый URL /v1, /v1/models или полный URL /chat/completions. API-ключ необязателен.");
        UpdateModelLength();
        UpdateModelEndpointWarning();
        SavePromptButton.Content = Localization.Text("Save", "Сохранить");
        ReloadPromptButton.Content = Localization.Text("Reload", "Перечитать");
        RestorePromptButton.Content = Localization.Text("Restore default", "Вернуть стандартный");

        StatusLabelRun.Text = Localization.Text("Status: ", "Статус: ");
        CurrentCommitLabelRun.Text = Localization.Text("Current commit: ", "Текущий коммит: ");
        StartButton.Content = Localization.Text("Start", "Старт");
        StopButton.Content = Localization.Text("Stop", "Стоп");
        OpenReportButton.Content = Localization.Text("Open report", "Открыть отчет");
        HideToTrayButton.Content = Localization.Text("Hide to tray", "Скрыть в трей");
        ExitButton.Content = Localization.Text("Exit", "Выход");

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
            new AuthenticationOption(
                "putty-key",
                Localization.Text("PuTTY Key File (.ppk)", "Ключ PuTTY из файла (.ppk)")),
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
            UpdateStartCommitButton();
        }
    }

    private void UpdateAuthenticationControls()
    {
        var mode = GetAuthenticationMode();
        var usesKeyFile = mode is "ssh-key" or "putty-key";
        SshKeyPathTextBox.IsEnabled = usesKeyFile;
        BrowseSshKeyButton.IsEnabled = usesKeyFile;
        PlinkPathTextBox.IsEnabled = mode == "putty-key";
        BrowsePlinkButton.IsEnabled = mode == "putty-key";
        SshKeyLabel.Text = mode == "putty-key"
            ? Localization.Text("PuTTY private key (.ppk)", "Приватный ключ PuTTY (.ppk)")
            : Localization.Text("SSH private key", "Приватный SSH-ключ");
        AuthenticationHelpTextBlock.Text = mode switch
        {
            "putty-key" => Localization.Text(
                "Select a .ppk private key and optionally the path to plink.exe. Leave the Plink path empty for auto-detection. Load an encrypted key into Pageant first.",
                "Выберите приватный ключ .ppk и при необходимости путь к plink.exe. Пустой путь включает автоматический поиск Plink. Зашифрованный ключ сначала загрузите в Pageant."),
            "ssh-key" => Localization.Text(
                "Select a private key file. Add its public key to the Git server. Use SSH Agent if the key has a passphrase.",
                "Выберите файл приватного ключа. Добавьте публичный ключ на Git-сервер. Для ключа с паролем используйте SSH Agent."),
            "https" => Localization.Text(
                "Uses credentials already stored by Git Credential Manager. The selected remote URL must start with https://.",
                "Используются учетные данные из Git Credential Manager. Адрес выбранного remote должен начинаться с https://."),
            _ => Localization.Text(
                "Uses keys loaded into Windows OpenSSH Agent. The selected remote must use an SSH URL.",
                "Используются ключи, загруженные в Windows OpenSSH Agent. Для выбранного remote нужен SSH-адрес.")
        };
    }

    private string GetAuthenticationMode() =>
        (AuthenticationComboBox.SelectedItem as AuthenticationOption)?.Code ?? "ssh-agent";

    private void BrowseSshKey_Click(object sender, RoutedEventArgs e)
    {
        var usesPutty = GetAuthenticationMode() == "putty-key";
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = usesPutty
                ? Localization.Text("Select a PuTTY private key", "Выберите приватный ключ PuTTY")
                : Localization.Text("Select an SSH private key", "Выберите приватный SSH-ключ"),
            Filter = usesPutty
                ? Localization.Text(
                    "PuTTY private keys|*.ppk|All files|*.*",
                    "Приватные ключи PuTTY|*.ppk|Все файлы|*.*")
                : Localization.Text(
                    "SSH private keys|id_*;*.pem;*.key|All files|*.*",
                    "Приватные SSH-ключи|id_*;*.pem;*.key|Все файлы|*.*")
        };
        if (dialog.ShowDialog(this) == true)
        {
            SshKeyPathTextBox.Text = dialog.FileName;
            SaveAuthenticationPreferences();
        }
    }

    private void BrowsePlink_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Localization.Text("Select Plink executable", "Выберите исполняемый файл Plink"),
            Filter = Localization.Text(
                "PuTTY Plink|plink.exe|Executable files|*.exe",
                "PuTTY Plink|plink.exe|Исполняемые файлы|*.exe")
        };
        if (dialog.ShowDialog(this) == true)
        {
            PlinkPathTextBox.Text = dialog.FileName;
            SaveAuthenticationPreferences();
        }
    }

    private void EndpointTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ModelConnection_Changed(sender, e);
        UpdateModelEndpointWarning();
    }

    private void ModelConnection_Changed(object sender, RoutedEventArgs e)
    {
        _modelRequestVersion++;
        _availableModels = [];
        if (ModelNameComboBox is null || ModelStatusTextBlock is null)
            return;
        var text = ModelNameComboBox.Text;
        ModelNameComboBox.ItemsSource = null;
        ModelNameComboBox.Text = text;
        ModelStatusTextBlock.Text = string.Empty;
        UpdateModelLength();
    }

    private void ModelName_Changed(object sender, TextChangedEventArgs e)
    {
        _modelRequestVersion++;
        if (ModelStatusTextBlock is not null)
            ModelStatusTextBlock.Text = string.Empty;
        UpdateModelLength();
    }

    private void UpdateModelLength()
    {
        if (ModelLengthTextBlock is null)
            return;
        var length = _availableModels.FirstOrDefault(model => model.Id == ModelNameComboBox.Text.Trim())?.MaxModelLength;
        ModelLengthTextBlock.Text = length is null ? string.Empty : Localization.Format(
            "Server context limit (max_model_len): {0} tokens. Informational only.",
            "Лимит контекста сервера (max_model_len): {0} токенов. Только для информации.", length);
    }

    private async void LoadModels_Click(object sender, RoutedEventArgs e)
    {
        var version = ++_modelRequestVersion;
        LoadModelsButton.IsEnabled = false;
        ModelStatusTextBlock.Text = Localization.Text("Loading models...", "Загрузка моделей...");
        try
        {
            var models = await _model.DiscoverModelsAsync(ReadProfileFromForm(false), CancellationToken.None);
            if (version != _modelRequestVersion || _exitRequested)
                return;
            var text = ModelNameComboBox.Text;
            _availableModels = models;
            ModelNameComboBox.ItemsSource = models.Select(model => model.Id).ToList();
            ModelNameComboBox.Text = text;
            UpdateModelLength();
            ModelStatusTextBlock.Text = models.Count == 0
                ? Localization.Text("No models returned. You can enter a model manually.", "Список моделей пуст. Можно ввести модель вручную.")
                : Localization.Format("Loaded {0} models. Select one or enter a name.", "Загружено моделей: {0}. Выберите модель или введите название.", models.Count);
        }
        catch (Exception exception)
        {
            if (version == _modelRequestVersion && !_exitRequested)
                ModelStatusTextBlock.Text = Localization.Format(
                    "Model discovery failed: {0}", "Не удалось загрузить модели: {0}", exception.Message);
        }
        finally
        {
            LoadModelsButton.IsEnabled = true;
        }
    }

    private void UpdateModelEndpointWarning()
    {
        if (ModelHttpWarningTextBlock is null)
            return;
        var endpointText = EndpointTextBox.Text.Trim();
        ModelHttpWarningTextBlock.Text = Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint) &&
                                         endpoint.Scheme == Uri.UriSchemeHttp &&
                                         !endpoint.IsLoopback
            ? Localization.Text(
                "Warning: HTTP is not encrypted. The API key and repository diff can be intercepted in transit.",
                "Предупреждение: HTTP не шифруется. API-ключ и diff репозитория могут быть перехвачены при передаче.")
            : string.Empty;
    }

    private void SshKeyPathTextBox_LostKeyboardFocus(
        object sender,
        System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        SaveAuthenticationPreferences();
        UpdateStartCommitButton();
    }

    private void SaveAuthenticationPreferences()
    {
        if (_loadingAuthentication)
            return;
        var settings = _configuration.LoadSettings();
        settings.GitAuthenticationMode = GetAuthenticationMode();
        settings.SshPrivateKeyPath = SshKeyPathTextBox.Text.Trim();
        settings.PlinkPath = PlinkPathTextBox.Text.Trim();
        _configuration.SaveSettings(settings);
    }

    private async void TestRemote_Click(object sender, RoutedEventArgs e)
    {
        var version = _repositoryVersion;
        TestRemoteButton.IsEnabled = false;
        RemoteAccessStatusTextBlock.Text = Localization.Text(
            "Testing repository access...",
            "Проверяется доступ к репозиторию...");
        try
        {
            var settings = ReadSettingsFromForm();
            var identity = GetRepositoryTestIdentity();
            _configuration.SaveSettings(settings);
            await _git.TestRemoteAccessAsync(
                settings.RepositoryPath, settings, CancellationToken.None);
            if (version != _repositoryVersion || _exitRequested || identity != GetRepositoryTestIdentity()) return;
            _testedRepositoryIdentity = identity;
            RemoteAccessStatusTextBlock.Text = Localization.Text(
                "Repository access confirmed.",
                "Доступ к репозиторию подтвержден.");
        }
        catch (Exception exception)
        {
            if (version != _repositoryVersion || _exitRequested) return;
            RemoteAccessStatusTextBlock.Text = Localization.Format(
                "Repository access failed: {0}",
                "Ошибка доступа к репозиторию: {0}",
                exception.Message);
        }
        finally
        {
            TestRemoteButton.IsEnabled = true;
            UpdateStartCommitButton();
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

    private void BrowseRepository_Click(object sender, RoutedEventArgs e)
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
        }
    }

    private async void RepositoryPath_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loadingRepository) return;
        _selectedBranch = string.Empty;
        await RefreshRepositoryInfoAsync();
    }

    private async void RefreshBranches_Click(object sender, RoutedEventArgs e) => await RefreshRepositoryInfoAsync();

    private async void Branch_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingRepository || BranchComboBox.SelectedItem is not string branch) return;
        _selectedBranch = branch;
        await RefreshRepositoryInfoAsync();
    }

    private async Task RefreshRepositoryInfoAsync()
    {
        var version = ++_repositoryVersion;
        var selected = _selectedBranch;
        _repositoryReady = false;
        _repositoryPath = string.Empty;
        _repositoryCommonGitDirectory = string.Empty;
        _repositoryWasKnown = false;
        UpdateStartCommitButton();
        _loadingRepository = true;
        BranchComboBox.ItemsSource = selected.Length == 0 ? Array.Empty<string>() : new[] { selected };
        BranchComboBox.SelectedItem = selected;
        _loadingRepository = false;
        RemoteTextBlock.Text = string.Empty;
        RemoteAccessStatusTextBlock.Text = string.Empty;
        RepositoryPathTextBox.ToolTip = null;
        try
        {
            var path = Path.GetFullPath(RepositoryPathTextBox.Text.Trim());
            await _git.ValidateRepositoryAsync(path, CancellationToken.None);
            var identity = await _git.GetRepositoryIdentityAsync(path, CancellationToken.None);
            path = identity.WorkTreeRoot;
            var branches = await _git.GetBranchesAsync(path, CancellationToken.None);
            if (version != _repositoryVersion || _exitRequested) return;
            // Keep a missing selection visible, but allow choosing another existing ref.
            _loadingRepository = true;
            BranchComboBox.ItemsSource = branches.Concat(selected.Length == 0 ? [] : new[] { selected }).Distinct(StringComparer.Ordinal).ToArray();
            BranchComboBox.SelectedItem = selected;
            _loadingRepository = false;
            var branch = await _git.ResolveBranchAsync(path, selected, CancellationToken.None);
            if (version != _repositoryVersion || _exitRequested) return;
            _selectedBranch = branch;
            _loadingRepository = true;
            BranchComboBox.ItemsSource = branches;
            BranchComboBox.SelectedItem = branch;
            _loadingRepository = false;
            var settings = _configuration.LoadSettings();
            settings.RepositoryPath = path;
            settings.BranchRef = branch;
            _configuration.SaveSettings(settings);
            var remote = await _git.GetSelectedRemoteSummaryAsync(path, branch, CancellationToken.None);
            var detectedMode = _authenticationNeedsDetection
                ? await _git.DetectAuthenticationModeAsync(path, CancellationToken.None, branch) : null;
            var wasKnown = await _stateStore.RegisterRepositoryAsync(
                identity.CommonGitDirectory, path, CancellationToken.None);
            if (!wasKnown) _repositoriesAwaitingTest.Add(identity.CommonGitDirectory);
            if (version != _repositoryVersion || _exitRequested) return;
            _loadingRepository = true;
            RepositoryPathTextBox.Text = path;
            RepositoryPathTextBox.ToolTip = identity.IsLinkedWorktree
                ? Localization.Format(
                    "Linked worktree. Shared Git data: {0}",
                    "Связанный worktree. Общие данные Git: {0}",
                    identity.CommonGitDirectory)
                : null;
            _loadingRepository = false;
            RemoteTextBlock.Text = remote.Length == 0
                ? Localization.Text("Not configured", "Не настроен")
                : remote;
            if (_authenticationNeedsDetection && detectedMode is not null)
            {
                LoadAuthenticationOptions(detectedMode);
            }
            _repositoryPath = path;
            _repositoryCommonGitDirectory = identity.CommonGitDirectory;
            // A local-only repository needs no remote authorization. Local
            // repository and branch validation is enough to select a baseline.
            _repositoryWasKnown = remote.Length == 0 ||
                wasKnown && !_repositoriesAwaitingTest.Contains(identity.CommonGitDirectory);
            _repositoryReady = true;
            UpdateStartCommitButton();
        }
        catch (Exception exception)
        {
            if (version != _repositoryVersion || _exitRequested) return;
            RemoteTextBlock.Text = exception.Message;
            UpdateStartCommitButton();
        }
    }

    private void ProfilesComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ModelConnection_Changed(sender, e);
        if (ProfilesComboBox.SelectedItem is not string name)
            return;
        var profile = _models.Profiles.FirstOrDefault(item => item.Name == name);
        if (profile is null)
            return;
        ProfileNameTextBox.Text = profile.Name;
        EndpointTextBox.Text = profile.Endpoint;
        ModelNameComboBox.Text = profile.Model;
        ApiKeyPasswordBox.Password = profile.ApiKey;
        ApiKeyEnvironmentTextBox.Text = profile.ApiKeyEnvironment;
        _models.ActiveProfile = profile.Name;
    }

    private void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        ProfilesComboBox.SelectedItem = null;
        ProfileNameTextBox.Clear();
        EndpointTextBox.Clear();
        ModelNameComboBox.Text = string.Empty;
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
        var version = ++_modelRequestVersion;
        TestModelButton.IsEnabled = false;
        ModelStatusTextBlock.Text = Localization.Text(
            "Testing connection...",
            "Проверяется подключение...");
        try
        {
            var status = await _model.TestConnectionAsync(
                ReadProfileFromForm(), CancellationToken.None);
            if (version == _modelRequestVersion && !_exitRequested)
                ModelStatusTextBlock.Text = status;
        }
        catch (Exception exception)
        {
            if (version == _modelRequestVersion && !_exitRequested)
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

    private ModelProfile ReadProfileFromForm(bool requireName = true)
    {
        var name = ProfileNameTextBox.Text.Trim();
        if (requireName && name.Length == 0)
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
            Model = ModelNameComboBox.Text.Trim(),
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
        if (_exitRequested || _manualReviewTask is { IsCompleted: false } || _settingStartCommit)
            return;

        try
        {
            var settings = ReadSettingsFromForm();
            var profile = ReadProfileFromForm();
            var revision = CommitShaTextBox.Text.Trim();
            _configuration.SaveSettings(settings);
            _configuration.SavePrompt(PromptTextBox.Text);
            ReviewCommitButton.IsEnabled = false;
            StartButton.IsEnabled = false;
            _trayStartItem.Enabled = false;
            SetRepositoryControls(false);
            _manualReviewCancellation = new CancellationTokenSource();
            _manualReviewTask = _runner.ReviewSingleCommitAsync(
                settings, profile, revision, _manualReviewCancellation.Token);
            UpdateStartCommitButton();
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
            SetRepositoryControls(!_runner.IsRunning);
            ReviewCommitButton.IsEnabled = !_runner.IsRunning;
            StartButton.IsEnabled = !_runner.IsRunning;
            _trayStartItem.Enabled = !_runner.IsRunning;
            UpdateStartCommitButton();
        }
    }

    private async void CommitSha_DropDownOpened(object? sender, EventArgs e)
    {
        var request = ++_commitListVersion;
        var version = _repositoryVersion;
        var path = _repositoryPath;
        var branch = _selectedBranch;
        var input = CommitShaTextBox.Text;
        CommitShaTextBox.ItemsSource = null;
        CommitShaTextBox.Text = input;
        if (!_repositoryReady || _exitRequested) return;
        try
        {
            var commits = await _git.GetRecentCommitsAsync(path, branch, CancellationToken.None);
            if (_exitRequested || request != _commitListVersion || version != _repositoryVersion ||
                path != _repositoryPath || branch != _selectedBranch) return;
            var currentInput = CommitShaTextBox.Text;
            CommitShaTextBox.ItemsSource = commits;
            CommitShaTextBox.Text = currentInput;
        }
        catch (Exception exception)
        {
            if (!_exitRequested && request == _commitListVersion && version == _repositoryVersion)
                ShowError(exception.Message);
        }
    }

    private async void SetStartCommit_Click(object sender, RoutedEventArgs e)
    {
        if (!CanSetStartCommit()) return;

        var version = _repositoryVersion;
        var repositoryPath = _repositoryPath;
        var repositoryIdentity = _repositoryCommonGitDirectory;
        var branch = _selectedBranch;
        _settingStartCommit = true;
        SetRepositoryControls(false);
        StartButton.IsEnabled = false;
        _trayStartItem.Enabled = false;
        ReviewCommitButton.IsEnabled = false;
        UpdateStartCommitButton();
        try
        {
            var revision = CommitShaTextBox.Text.Trim();
            var sha = await _git.ResolveCommitAsync(repositoryPath, revision, CancellationToken.None);
            var head = await _git.GetBranchHeadAsync(repositoryPath, branch, CancellationToken.None);
            if (!await _git.IsAncestorAsync(repositoryPath, sha, head, CancellationToken.None))
                throw new InvalidOperationException(Localization.Text(
                    "The selected commit is not an ancestor of the selected branch tip.",
                    "Выбранный коммит не является предком вершины выбранной ветки."));
            if (version != _repositoryVersion || repositoryPath != _repositoryPath ||
                repositoryIdentity != _repositoryCommonGitDirectory || branch != _selectedBranch)
                throw new InvalidOperationException(Localization.Text(
                    "Repository selection changed. Select the start commit again.",
                    "Выбор репозитория изменился. Выберите стартовый коммит снова."));
            await _stateStore.SetStartCommitAsync(repositoryIdentity, branch, sha, CancellationToken.None);
            CommitRun.Text = sha[..8];
            AppendLog(Localization.Format(
                "Automatic review will start with commit {0} on {1}.",
                "Автоматическая проверка начнется с коммита {0} в {1}.",
                sha[..8],
                branch));
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
        finally
        {
            _settingStartCommit = false;
            var idle = !_runner.IsRunning && _manualReviewTask is not { IsCompleted: false };
            SetRepositoryControls(idle);
            StartButton.IsEnabled = idle;
            _trayStartItem.Enabled = idle;
            ReviewCommitButton.IsEnabled = idle;
            UpdateStartCommitButton();
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e) => await StartReviewAsync();

    private async Task StartReviewAsync()
    {
        var repositoryVersion = _repositoryVersion;
        if (_exitRequested || _runner.IsRunning || _manualReviewTask is { IsCompleted: false } || _settingStartCommit)
            return;
        StartButton.IsEnabled = false;
        _trayStartItem.Enabled = false;
        ReviewCommitButton.IsEnabled = false;
        SetRepositoryControls(false);
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
            settings.BranchRef = await _git.ResolveBranchAsync(settings.RepositoryPath, settings.BranchRef, CancellationToken.None);
            if (_exitRequested) return;
            if (repositoryVersion != _repositoryVersion)
                throw new InvalidOperationException(Localization.Text("Repository selection changed. Start again.", "Выбор репозитория изменился. Запустите снова."));
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
        if (!_repositoryReady)
            throw new InvalidOperationException(Localization.Text(
                "Wait for repository refresh and select an existing branch.",
                "Дождитесь обновления репозитория и выберите существующую ветку."));
        var path = _repositoryPath;
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
            RepositoryPath = path,
            BranchRef = _selectedBranch,
            PollIntervalSeconds = seconds,
            PullEnabled = PullEnabledCheckBox.IsChecked == true,
            Language = Localization.Language,
            GitAuthenticationMode = GetAuthenticationMode(),
            SshPrivateKeyPath = SshKeyPathTextBox.Text.Trim(),
            PlinkPath = PlinkPathTextBox.Text.Trim()
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
            var branch = await _git.ResolveBranchAsync(path, _selectedBranch, CancellationToken.None);
            var identity = await _git.GetRepositoryIdentityAsync(path, CancellationToken.None);
            if (_exitRequested) return;
            _reportWriter.Open(identity.WorkTreeRoot, branch, identity.CommonGitDirectory);
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private void HideToTray_Click(object sender, RoutedEventArgs e) => HideToTray();

    private async void Exit_Click(object sender, RoutedEventArgs e) => await ExitApplicationAsync();

    private void HideToTray()
    {
        _logWindow?.Hide();
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
        if (_exitRequested) return;
        _exitRequested = true;
        IsEnabled = false;
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
        _logTimer.Stop();
        RefreshLogs();
        _logWindow?.Close();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    private void SetRunningControls(bool running)
    {
        SetRepositoryControls(!running && _manualReviewTask is not { IsCompleted: false });
        StartButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        _trayStartItem.Enabled = !running;
        _trayStopItem.Enabled = running;
        ReviewCommitButton.IsEnabled = !running && _manualReviewTask is not { IsCompleted: false };
        UpdateStartCommitButton();
    }

    private void SetRepositoryControls(bool enabled)
    {
        RepositoryPathTextBox.IsEnabled = enabled;
        BrowseButton.IsEnabled = enabled;
        BranchComboBox.IsEnabled = enabled;
        RefreshBranchesButton.IsEnabled = enabled;
    }

    private string GetRepositoryTestIdentity() => string.Join('\n',
        _repositoryCommonGitDirectory,
        _selectedBranch,
        GetAuthenticationMode(),
        SshKeyPathTextBox.Text.Trim(),
        PlinkPathTextBox.Text.Trim());

    private bool CanSetStartCommit() =>
        !_exitRequested &&
        _repositoryReady &&
        !_runner.IsRunning &&
        _manualReviewTask is not { IsCompleted: false } &&
        !_settingStartCommit &&
        (_repositoryWasKnown || _testedRepositoryIdentity == GetRepositoryTestIdentity());

    private void UpdateStartCommitButton() => SetStartCommitButton.IsEnabled = CanSetStartCommit();

    private void SetStatus(string status)
    {
        StatusRun.Text = status;
        _trayIcon.Text = TruncateTrayText($"Git Reviewer - {status.ToLowerInvariant()}");
    }

    private void AppendLog(string message)
    {
        _journal.Append(message);
    }

    private void RefreshLogs()
    {
        if (_journal.Snapshot() is { } journal)
        {
            LogTextView.Update(LogTextBox, journal);
        }
        if (_logWindow is not null && _details.Snapshot() is { } details) _logWindow.SetText(details);
    }

    private void Dispatch(Action action)
    {
        if (_exitRequested || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        if (Dispatcher.CheckAccess())
            action();
        else
        {
            try { Dispatcher.BeginInvoke(() => { if (!_exitRequested) action(); }); }
            catch (InvalidOperationException) { }
        }
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (_logWindow is null)
        {
            _logWindow = new LogWindow { Owner = this, Title = Localization.Text("Log", "Лог") };
            _logWindow.Closed += (_, _) => _logWindow = null;
        }
        _logWindow.SetText(_details.Snapshot(true)!);
        _logWindow.Show();
        _logWindow.Activate();
    }

    private void AppendProgress(ReviewProgress progress)
    {
        var model = new string(progress.Model.Where(c => !char.IsControl(c)).Take(160).ToArray());
        var commit = progress.Commit.Length is > 0 and <= 40 && progress.Commit.All(Uri.IsHexDigit) ? progress.Commit : "-";
        var detail = new string(progress.Detail.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        if (detail.Length > 2000)
            detail = detail[..950] + " ... " + detail[^1045..];
        var entry = $"{model} | {commit} | {progress.Stage}{(detail.Length > 0 ? " | " + detail : "")}";
        _details.Append(entry);
        // Transport events stay in the detailed log; the journal describes review activity.
        if (progress.Stage is ReviewStage.Waiting or ReviewStage.Response or ReviewStage.Parsing or ReviewStage.SavingCursor)
            return;
        var description = progress.Stage switch
        {
            ReviewStage.Started => Localization.Text("Commit review started", "Начата проверка коммита"),
            ReviewStage.PreparingDiff => Localization.Text("Preparing commit changes", "Подготовка изменений коммита"),
            ReviewStage.Request => Localization.Text("Model is analyzing the available context", "Модель анализирует доступный контекст"),
            ReviewStage.Tool => Localization.Text("Calling Git tool", "Вызов Git-инструмента"),
            ReviewStage.ToolRejected => Localization.Text("Git tool could not complete the call", "Не удалось выполнить вызов Git-инструмента"),
            ReviewStage.FormatCorrection => Localization.Text("Requesting report format correction", "Запрошено исправление формата отчёта"),
            ReviewStage.Report => Localization.Text("Saving review report", "Сохранение отчёта проверки"),
            ReviewStage.Completed => Localization.Text("Commit review completed", "Проверка коммита завершена"),
            ReviewStage.Failed => Localization.Text("Commit review failed", "Ошибка проверки коммита"),
            ReviewStage.Canceled => Localization.Text("Commit review canceled", "Проверка коммита отменена"),
            _ => progress.Stage.ToString()
        };
        var shortCommit = commit[..Math.Min(8, commit.Length)];
        _journal.Append($"{model} | {shortCommit} | {description}{(detail.Length > 0 ? " | " + detail : "")}");
    }

    private void NotifyReviewed(CommitReviewed reviewed)
    {
        if (_exitRequested) return;
        var result = reviewed.EmptyDiff
            ? Localization.Text("Empty diff; no model request.", "Пустой diff; без запроса к модели.")
            : reviewed.HasUnstructuredResponse
                ? Localization.Text("Unstructured response; inspect the report.", "Неструктурированный ответ; проверьте отчет.")
                : Localization.Format("Findings: {0}.", "Замечаний: {0}.", reviewed.FindingCount);
        try
        {
            _trayIcon.ShowBalloonTip(5000, Localization.Text("Commit review completed", "Проверка коммита завершена"),
                $"{reviewed.Sha[..Math.Min(8, reviewed.Sha.Length)]} | {reviewed.BranchRef}\n{result}",
                reviewed.HasUnstructuredResponse ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
        }
        catch (Exception) { /* Notifications are best-effort and must not affect persisted reviews. */ }
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
