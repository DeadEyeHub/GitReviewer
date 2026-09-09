using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GitReviewer;

public sealed class LogWindow : Window
{
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
        Content = _text;
    }

    public void SetText(string text)
    {
        _text.Text = text;
        _text.ScrollToEnd();
    }
}
