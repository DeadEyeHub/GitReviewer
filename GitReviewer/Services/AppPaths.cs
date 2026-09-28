using System.Reflection;
using System.Text;

namespace GitReviewer.Services;

public static class AppPaths
{
    public static string DataDirectory { get; } = ResolveDataDirectory();
    public static string ModelsConfig => Path.Combine(DataDirectory, "models.conf");
    public static string SettingsConfig => Path.Combine(DataDirectory, "settings.conf");
    public static string StateFile => Path.Combine(DataDirectory, "state.json");
    public static string ReportsDirectory => Path.Combine(DataDirectory, "reports");
    public static string JournalLog => Path.Combine(DataDirectory, "journal.log");
    public static string ModelLog => Path.Combine(DataDirectory, "model-log.log");

    private static string ResolveDataDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("GITREVIEWER_DATA_DIR");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitReviewer")
            : Path.GetFullPath(configured);
    }

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ReportsDirectory);
        if (!File.Exists(ModelsConfig))
            File.WriteAllText(ModelsConfig, ReadTemplate("models.example.conf"), Encoding.UTF8);
    }

    public static string ReadTemplate(string fileName)
    {
        var resourceName = $"GitReviewer.Templates.{fileName}";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded template not found: {fileName}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
