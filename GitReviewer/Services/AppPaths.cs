namespace GitReviewer.Services;

public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GitReviewer");

    public static string ModelsConfig => Path.Combine(DataDirectory, "models.conf");
    public static string SettingsConfig => Path.Combine(DataDirectory, "settings.conf");
    public static string StateFile => Path.Combine(DataDirectory, "state.json");
    public static string SystemPrompt => Path.Combine(DataDirectory, "system-prompt.txt");
    public static string ReportsDirectory => Path.Combine(DataDirectory, "reports");

    public static string Template(string fileName) => Path.Combine(AppContext.BaseDirectory, fileName);

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ReportsDirectory);

        CopyIfMissing(Template("models.example.conf"), ModelsConfig);
        CopyIfMissing(Template("system-prompt.example.txt"), SystemPrompt);
        MigrateOriginalDefaultPrompt();
    }

    private static void CopyIfMissing(string source, string destination)
    {
        if (!File.Exists(destination) && File.Exists(source))
            File.Copy(source, destination);
    }

    private static void MigrateOriginalDefaultPrompt()
    {
        const string originalDefault = """
            Ты проверяешь один Git-коммит.

            Ищи ошибки корректности, внесенные представленным изменением.
            Учитывай добавленные, измененные и удаленные строки.
            Проверяй неправильные вычисления, потерю проверок, неверные условия,
            ошибки обработки null, исключения и нежелательное изменение поведения.

            Не проводи аудит старой версии проекта. Контекстные строки используются
            только для понимания diff. Лучше сообщить о вероятной ошибке, чем пропустить
            опасное изменение.
            """;

        var template = Template("system-prompt.example.txt");
        if (!File.Exists(SystemPrompt) || !File.Exists(template))
            return;
        var current = File.ReadAllText(SystemPrompt).Replace("\r\n", "\n").Trim();
        if (current.Equals(originalDefault.Replace("\r\n", "\n").Trim(), StringComparison.Ordinal))
            File.Copy(template, SystemPrompt, true);
    }
}
