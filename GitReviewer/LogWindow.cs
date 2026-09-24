using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GitReviewer;

public sealed class LogWindow : Window
{
    private readonly System.Windows.Controls.Button _clear = new()
    {
        Margin = new Thickness(8), Padding = new Thickness(12, 5, 12, 5),
        HorizontalAlignment = System.Windows.HorizontalAlignment.Right
    };
    public event Action? ClearRequested;
    private readonly System.Windows.Controls.TextBox _text = new()
    {
        IsReadOnly = true, AcceptsReturn = true, FontFamily = new System.Windows.Media.FontFamily("Consolas"),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
    };

    public LogWindow()
    {
        Width = 900;
        Height = 420;
        MinWidth = 420;
        MinHeight = 220;
        var panel = new DockPanel();
        DockPanel.SetDock(_clear, Dock.Top);
        panel.Children.Add(_clear);
        panel.Children.Add(_text);
        Content = panel;
        _clear.Click += (_, _) => ClearRequested?.Invoke();
        ApplyLanguage();
    }

    public void ApplyLanguage()
    {
        _clear.Content = Services.Localization.Text("Clear log", "Очистить лог");
        _clear.ToolTip = Services.Localization.Text(
            "Delete the detailed log and its rotated history. Reports and the journal are kept.",
            "Удалить подробный лог и его архив. Отчёты и журнал сохраняются.");
    }

    public void SetText(string text)
    {
        LogTextView.Update(_text, text);
    }
}
