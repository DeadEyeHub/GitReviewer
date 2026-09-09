using System.Reflection;
using System.Text;

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

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ReportsDirectory);

        WriteIfMissing(ReadTemplate("models.example.conf"), ModelsConfig);
        WriteIfMissing(ReadTemplate("system-prompt.example.txt"), SystemPrompt);
        MigrateLegacyDefaultPrompts();
    }

    public static string ReadTemplate(string fileName)
    {
        var resourceName = $"GitReviewer.Templates.{fileName}";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded template not found: {fileName}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void WriteIfMissing(string content, string destination)
    {
        if (!File.Exists(destination))
            File.WriteAllText(destination, content, Encoding.UTF8);
    }

    private static void MigrateLegacyDefaultPrompts()
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

        if (!File.Exists(SystemPrompt))
            return;
        var current = File.ReadAllText(SystemPrompt).Replace("\r\n", "\n").Trim();
        const string previousEnglish = """
            You review one Git commit at a time.

            Find correctness bugs introduced by the presented change. Consider added,
            modified, and deleted lines. Look for incorrect calculations, removed
            validation, wrong conditions, null-handling errors, exceptions, and unintended
            behavior changes.

            Do not audit the old version of the project. Context lines are provided only to
            help you understand the diff. It is better to report a plausible bug than to
            miss a dangerous change.
            """;
        const string previousRussian = """
            Ты проверяешь один Git-коммит.

            Ищи ошибки корректности, внесенные представленным изменением. Учитывай
            добавленные, измененные и удаленные строки. Проверяй неправильные вычисления,
            потерю проверок, неверные условия, ошибки обработки null, исключения и
            нежелательное изменение поведения.

            Не проводи аудит старой версии проекта. Контекстные строки даны только для
            понимания diff. Лучше сообщить о вероятной ошибке, чем пропустить опасное
            изменение.
            """;
        if (current.Equals(previousRussian.Replace("\r\n", "\n").Trim(), StringComparison.Ordinal))
            File.WriteAllText(SystemPrompt, ReadTemplate("system-prompt.ru.example.txt"), Encoding.UTF8);
        else if (current.Equals(originalDefault.Replace("\r\n", "\n").Trim(), StringComparison.Ordinal) ||
                 current.Equals(previousEnglish.Replace("\r\n", "\n").Trim(), StringComparison.Ordinal))
            File.WriteAllText(SystemPrompt, ReadTemplate("system-prompt.example.txt"), Encoding.UTF8);
    }
}
