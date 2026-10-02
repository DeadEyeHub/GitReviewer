using TextBox = System.Windows.Controls.TextBox;
using ListBox = System.Windows.Controls.ListBox;
using Button = System.Windows.Controls.Button;
using Localization = GitReviewer.Services.Localization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GitReviewer.Models;
using GitReviewer.Services;

namespace GitReviewer;

public sealed class CommitSearchWindow : Window
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TextBox _query = new() { Margin = new Thickness(0, 8, 0, 8) };
    private readonly ListBox _results = new() { DisplayMemberPath = nameof(CommitChoice.Display) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private IReadOnlyList<CommitChoice> _commits = [];
    public string SelectedSha { get; private set; } = string.Empty;

    public CommitSearchWindow(GitService git, string path, string branch)
    {
        Title = Localization.Text("Find commit", "Поиск коммита");
        Width = 850; Height = 520; MinWidth = 500; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new Thickness(16) };
        Content = panel;
        var label = new TextBlock { Text = Localization.Text(
            "Search recent commits by SHA or description; enter a full or short SHA for an older commit.",
            "Поиск последних коммитов по хешу или описанию; для более старого введите полный или короткий SHA."), TextWrapping = TextWrapping.Wrap };
        DockPanel.SetDock(label, Dock.Top); panel.Children.Add(label);
        DockPanel.SetDock(_query, Dock.Top); panel.Children.Add(_query);
        var select = new Button { Content = Localization.Text("Select", "Выбрать"), Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        DockPanel.SetDock(select, Dock.Bottom); panel.Children.Add(select);
        DockPanel.SetDock(_status, Dock.Bottom); panel.Children.Add(_status);
        panel.Children.Add(_results);
        _query.TextChanged += (_, _) => Filter();
        select.Click += (_, _) => SelectCommit();
        _results.MouseDoubleClick += (_, _) => { if (_results.SelectedItem is CommitChoice) SelectCommit(); };
        _results.KeyDown += (_, e) => { if (e.Key == Key.Enter) SelectCommit(); };
        Closed += (_, _) => { _cancellation.Cancel(); _cancellation.Dispose(); };
        Loaded += async (_, _) =>
        {
            _query.Focus();
            _status.Text = Localization.Text("Loading…", "Загрузка…");
            try
            {
                _commits = await git.GetRecentCommitsAsync(path, branch, _cancellation.Token);
                Filter();
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { _status.Text = error.Message; }

        };
    }

    private void Filter()
    {
        var query = _query.Text.Trim();
        var matches = _commits.Where(c => c.Sha.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            c.Subject.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        _results.ItemsSource = matches;
        _status.Text = Localization.Format("Matching commits: {0}", "Найдено коммитов: {0}", matches.Count);
    }

    private void SelectCommit()
    {
        var sha = (_results.SelectedItem as CommitChoice)?.Sha ?? _query.Text.Trim();
        if (sha.Length is < 4 or > 40 || !sha.All(Uri.IsHexDigit)) return;
        SelectedSha = sha;
        DialogResult = true;
    }
}

