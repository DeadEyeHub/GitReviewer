namespace GitReviewer.Services;

public static class Localization
{
    private static string _language = "en";

    public static string Language => _language;
    public static bool IsRussian => _language == "ru";

    public static void SetLanguage(string? language) =>
        _language = language?.Equals("ru", StringComparison.OrdinalIgnoreCase) == true ? "ru" : "en";

    public static string Text(string english, string russian) => IsRussian ? russian : english;

    public static string Format(string english, string russian, params object?[] arguments) =>
        string.Format(IsRussian ? russian : english, arguments);
}
