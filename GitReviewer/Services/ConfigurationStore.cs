using System.Globalization;
using System.Text;
using GitReviewer.Models;

namespace GitReviewer.Services;

public sealed class ConfigurationStore
{
    public ConfigurationStore() => AppPaths.EnsureCreated();

    private static string ProjectsPath => Path.Combine(AppPaths.DataDirectory, "projects.json");

    public ProjectCatalog LoadProjects()
    {
        if (!File.Exists(ProjectsPath))
            return new ProjectCatalog { Projects = [LoadLegacySettings()] };
        var catalog = System.Text.Json.JsonSerializer.Deserialize<ProjectCatalog>(File.ReadAllText(ProjectsPath))
            ?? throw new InvalidDataException("Invalid project catalog.");
        ValidateProjects(catalog);
        return catalog;
    }

    private static void ValidateProjects(ProjectCatalog catalog)
    {
        if (catalog.Version != 1 || catalog.Projects is null || catalog.Projects.Count == 0 ||
            catalog.ActiveIndex < 0 || catalog.ActiveIndex >= catalog.Projects.Count ||
            catalog.Projects.Any(p => p is null || p.RepositoryPath is null || p.BranchRef is null))
            throw new InvalidDataException("Invalid project catalog. The existing file has been preserved.");
    }

    public void SaveProjects(ProjectCatalog catalog)
    {
        ValidateProjects(catalog);
        var path = ProjectsPath;
        using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            System.Text.Json.JsonSerializer.Serialize(stream, catalog);
            stream.Flush(true);
        }
        File.Move(path + ".tmp", path, true);
    }

    public void InitializeProjects()
    {
        var catalog = LoadProjects();
        if (!File.Exists(ProjectsPath)) SaveProjects(catalog);
    }

    public AppSettings LoadSettings() => File.Exists(ProjectsPath)
        ? LoadActiveProject() : LoadLegacySettings();

    private AppSettings LoadActiveProject()
    {
        var catalog = LoadProjects();
        return catalog.Projects[catalog.ActiveIndex];
    }

    private AppSettings LoadLegacySettings()
    {
        var settings = new AppSettings();
        if (!File.Exists(AppPaths.SettingsConfig))
            return settings;

        foreach (var (key, value) in ReadSimpleValues(AppPaths.SettingsConfig))
        {
            switch (key.ToLowerInvariant())
            {
                case "repository_path":
                    settings.RepositoryPath = value;
                    break;
                case "branch_ref":
                    settings.BranchRef = value;
                    break;
                case "poll_interval_seconds" when int.TryParse(value, out var seconds):
                    settings.PollIntervalSeconds = Math.Clamp(seconds, 5, 86_400);
                    break;
                case "pull_enabled" when bool.TryParse(value, out var enabled):
                    settings.PullEnabled = enabled;
                    break;
                case "language":
                    settings.Language = value.Equals("ru", StringComparison.OrdinalIgnoreCase) ? "ru" : "en";
                    break;
                case "fetch_submodules" when bool.TryParse(value, out var modules):
                    settings.FetchSubmodules = modules;
                    break;
                case "quiet_hours_enabled" when bool.TryParse(value, out var quiet): settings.QuietHoursEnabled = quiet; break;
                case "quiet_hours_start": settings.QuietHoursStart = value; break;
                case "quiet_hours_end": settings.QuietHoursEnd = value; break;
                case "git_authentication_mode":
                    settings.GitAuthenticationMode = value is "ssh-agent" or "ssh-key" or "putty-key" or "https"
                        ? value
                        : "auto";
                    break;
                case "ssh_private_key_path":
                    settings.SshPrivateKeyPath = value;
                    break;
                case "plink_path":
                    settings.PlinkPath = value;
                    break;
            }
        }

        return settings;
    }

    public void SaveSettings(AppSettings settings)
    {
        _ = new DailyModelSchedule(settings);
        if (File.Exists(ProjectsPath))
        {
            var catalog = LoadProjects();
            catalog.Projects[catalog.ActiveIndex] = settings;
            // Interface language is application-wide, unlike Git/repository settings.
            foreach (var project in catalog.Projects) project.Language = settings.Language;
            SaveProjects(catalog);
            return;
        }
        var text = new StringBuilder()
            .AppendLine($"repository_path={settings.RepositoryPath}")
            .AppendLine($"branch_ref={settings.BranchRef}")
            .AppendLine($"poll_interval_seconds={settings.PollIntervalSeconds.ToString(CultureInfo.InvariantCulture)}")
            .AppendLine($"pull_enabled={settings.PullEnabled.ToString().ToLowerInvariant()}")
            .AppendLine($"fetch_submodules={settings.FetchSubmodules.ToString().ToLowerInvariant()}")
            .AppendLine($"quiet_hours_enabled={settings.QuietHoursEnabled.ToString().ToLowerInvariant()}")
            .AppendLine($"quiet_hours_start={settings.QuietHoursStart}")
            .AppendLine($"quiet_hours_end={settings.QuietHoursEnd}")
            .AppendLine($"language={settings.Language}")
            .AppendLine($"git_authentication_mode={settings.GitAuthenticationMode}")
            .AppendLine($"ssh_private_key_path={settings.SshPrivateKeyPath}")
            .AppendLine($"plink_path={settings.PlinkPath}")
            .ToString();
        File.WriteAllText(AppPaths.SettingsConfig, text, Encoding.UTF8);
    }

    public ModelsConfiguration LoadModels()
    {
        var result = new ModelsConfiguration();
        if (!File.Exists(AppPaths.ModelsConfig))
            return result;

        ModelProfile? current = null;
        foreach (var rawLine in File.ReadLines(AppPaths.ModelsConfig))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = new ModelProfile { Name = line[1..^1].Trim() };
                if (current.Name.Length > 0)
                    result.Profiles.Add(current);
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator < 1)
                continue;

            var key = line[..separator].Trim().ToLowerInvariant();
            var value = line[(separator + 1)..].Trim();
            if (current is null)
            {
                if (key == "active")
                    result.ActiveProfile = value;
                continue;
            }

            switch (key)
            {
                case "endpoint": current.Endpoint = value; break;
                case "model": current.Model = value; break;
                case "api_key": current.ApiKey = value; break;
                case "api_key_environment": current.ApiKeyEnvironment = value; break;
                case "parameters":
                    current.Parameters = System.Text.Json.JsonSerializer.Deserialize<ModelParameters>(value)
                        ?? throw new InvalidDataException("Invalid model parameters.");
                    current.Parameters.Validate();
                    break;
            }
        }

        return result;
    }

    public void SaveModels(ModelsConfiguration configuration)
    {
        foreach (var profile in configuration.Profiles) profile.Parameters.Validate();
        var text = new StringBuilder()
            .AppendLine("# Local model profiles. This file must not be committed.")
            .AppendLine($"active={configuration.ActiveProfile}")
            .AppendLine();

        foreach (var profile in configuration.Profiles)
        {
            text.AppendLine($"[{profile.Name}]")
                .AppendLine($"endpoint={profile.Endpoint}")
                .AppendLine($"model={profile.Model}")
                .AppendLine($"api_key={profile.ApiKey}")
                .AppendLine($"api_key_environment={profile.ApiKeyEnvironment}")
                .AppendLine($"parameters={System.Text.Json.JsonSerializer.Serialize(profile.Parameters)}")
                .AppendLine();
        }

        File.WriteAllText(AppPaths.ModelsConfig, text.ToString(), Encoding.UTF8);
    }

    private static string CustomPromptPath => Path.Combine(AppPaths.DataDirectory, $"system-prompt.custom.{Localization.Language}.txt");
    public string LoadPrompt() => File.Exists(CustomPromptPath) ? File.ReadAllText(CustomPromptPath, Encoding.UTF8)
        : AppPaths.ReadTemplate(Localization.Language == "ru" ? "system-prompt.ru.example.txt" : "system-prompt.example.txt");
    public void SavePrompt(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 32000)
            throw new InvalidOperationException(Localization.Text("Prompt must contain 1–32000 characters.", "Промпт должен содержать от 1 до 32000 символов."));
        File.WriteAllText(CustomPromptPath + ".tmp", text, Encoding.UTF8);
        File.Move(CustomPromptPath + ".tmp", CustomPromptPath, true);
    }
    public void ResetPrompt() => File.Delete(CustomPromptPath);

    private static IEnumerable<(string Key, string Value)> ReadSimpleValues(string path)
    {
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
                continue;
            var separator = line.IndexOf('=');
            if (separator > 0)
                yield return (line[..separator].Trim(), line[(separator + 1)..].Trim());
        }
    }
}
