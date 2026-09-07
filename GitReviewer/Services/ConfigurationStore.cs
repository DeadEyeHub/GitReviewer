using System.Globalization;
using System.Text;
using GitReviewer.Models;

namespace GitReviewer.Services;

public sealed class ConfigurationStore
{
    public ConfigurationStore() => AppPaths.EnsureCreated();

    public AppSettings LoadSettings()
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
                case "poll_interval_seconds" when int.TryParse(value, out var seconds):
                    settings.PollIntervalSeconds = Math.Clamp(seconds, 5, 86_400);
                    break;
                case "pull_enabled" when bool.TryParse(value, out var enabled):
                    settings.PullEnabled = enabled;
                    break;
                case "language":
                    settings.Language = value.Equals("ru", StringComparison.OrdinalIgnoreCase) ? "ru" : "en";
                    break;
                case "git_authentication_mode":
                    settings.GitAuthenticationMode = value is "ssh-agent" or "ssh-key" or "https"
                        ? value
                        : "auto";
                    break;
                case "ssh_private_key_path":
                    settings.SshPrivateKeyPath = value;
                    break;
            }
        }

        return settings;
    }

    public void SaveSettings(AppSettings settings)
    {
        var text = new StringBuilder()
            .AppendLine($"repository_path={settings.RepositoryPath}")
            .AppendLine($"poll_interval_seconds={settings.PollIntervalSeconds.ToString(CultureInfo.InvariantCulture)}")
            .AppendLine($"pull_enabled={settings.PullEnabled.ToString().ToLowerInvariant()}")
            .AppendLine($"language={settings.Language}")
            .AppendLine($"git_authentication_mode={settings.GitAuthenticationMode}")
            .AppendLine($"ssh_private_key_path={settings.SshPrivateKeyPath}")
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
            }
        }

        return result;
    }

    public void SaveModels(ModelsConfiguration configuration)
    {
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
                .AppendLine();
        }

        File.WriteAllText(AppPaths.ModelsConfig, text.ToString(), Encoding.UTF8);
    }

    public string LoadPrompt() => File.Exists(AppPaths.SystemPrompt)
        ? File.ReadAllText(AppPaths.SystemPrompt, Encoding.UTF8)
        : string.Empty;

    public void SavePrompt(string prompt) =>
        File.WriteAllText(AppPaths.SystemPrompt, prompt, Encoding.UTF8);

    public void RestoreDefaultPrompt(string language)
    {
        var templateName = language.Equals("ru", StringComparison.OrdinalIgnoreCase)
            ? "system-prompt.ru.example.txt"
            : "system-prompt.example.txt";
        var template = AppPaths.Template(templateName);
        if (!File.Exists(template))
            throw new FileNotFoundException(Localization.Text(
                "The system prompt template was not found.",
                "Шаблон системного промпта не найден."), template);
        File.Copy(template, AppPaths.SystemPrompt, true);
    }

    public bool SwitchDefaultPromptLanguage(string language, string editorPrompt)
    {
        if (!File.Exists(AppPaths.SystemPrompt))
            return false;
        var current = NormalizePrompt(editorPrompt);
        var englishTemplate = AppPaths.Template("system-prompt.example.txt");
        var russianTemplate = AppPaths.Template("system-prompt.ru.example.txt");
        var matchesDefault = new[] { englishTemplate, russianTemplate }
            .Where(File.Exists)
            .Select(path => NormalizePrompt(File.ReadAllText(path, Encoding.UTF8)))
            .Any(prompt => prompt.Equals(current, StringComparison.Ordinal));
        if (!matchesDefault)
            return false;

        RestoreDefaultPrompt(language);
        return true;
    }

    private static string NormalizePrompt(string prompt) =>
        prompt.Replace("\r\n", "\n").Trim();

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
