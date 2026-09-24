using System.Windows.Controls;

namespace GitReviewer;

internal static class LogTextView
{
    public static void Update(System.Windows.Controls.TextBox box, string text)
    {
        var previous = box.Text;
        if (previous == text) return;
        var followEnd = previous.Length == 0 ||
            box.VerticalOffset + box.ViewportHeight >= box.ExtentHeight - 1;
        var vertical = box.VerticalOffset;
        var horizontal = box.HorizontalOffset;
        var selectionStart = box.SelectionStart;
        var selectionLength = box.SelectionLength;
        var firstLine = box.GetFirstVisibleLineIndex();
        var anchor = firstLine >= 0 ? box.GetCharacterIndexFromLineIndex(firstLine) : -1;
        var relocatedAnchor = -1;

        if (text.StartsWith(previous, StringComparison.Ordinal))
            box.AppendText(text[previous.Length..]);
        else
        {
            // A bounded log snapshot can drop old lines. Keep the visible line
            // anchored when it is still present in the retained tail.
            if (anchor >= 0 && anchor < previous.Length)
            {
                var end = previous.IndexOf('\n', anchor);
                if (end < 0) end = previous.Length;
                var visibleLine = previous[anchor..end];
                if (visibleLine.Length > 0)
                    relocatedAnchor = text.IndexOf(visibleLine, StringComparison.Ordinal);
            }
            box.Text = text;
            if (relocatedAnchor >= 0) selectionStart += relocatedAnchor - anchor;
        }

        selectionStart = Math.Clamp(selectionStart, 0, text.Length);
        box.Select(selectionStart, Math.Min(selectionLength, text.Length - selectionStart));
        // Measure the new content before scrolling: scrolling against the old
        // extent can otherwise leave the view at the top after a text reset.
        box.UpdateLayout();
        if (followEnd) box.ScrollToEnd();
        else if (relocatedAnchor >= 0)
            box.ScrollToLine(box.GetLineIndexFromCharacterIndex(relocatedAnchor));
        else box.ScrollToVerticalOffset(vertical);
        box.ScrollToHorizontalOffset(horizontal);
    }
}
