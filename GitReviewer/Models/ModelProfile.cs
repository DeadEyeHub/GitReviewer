using System.ComponentModel.DataAnnotations;

namespace GitReviewer.Models;

public sealed class ModelProfile
{
    public string Name { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ApiKeyEnvironment { get; set; } = string.Empty;

    public ModelParameters Parameters { get; set; } = new();
    public ModelProfile Clone()
    {
        var copy = (ModelProfile)MemberwiseClone();
        copy.Parameters = Parameters.Clone();
        return copy;
    }
}

public sealed class ModelParameters
{
    [Range(1, 240), Display(Name = "Review timeout (minutes)", Description = "Время проверки коммита (минуты)")]
    public int ReviewMinutes { get; set; } = 15;
    [Range(1, 1440), Display(Name = "Response inactivity threshold (minutes)", Description = "Порог отсутствия ответов (минуты)")]
    public int ResponseInactivityMinutes { get; set; } = 5;
    [Range(1, 1440), Display(Name = "Availability check interval (minutes)", Description = "Интервал проверки доступности (минуты)")]
    public int AvailabilityCheckMinutes { get; set; } = 5;
    [Range(0, 100), Display(Name = "Retries after failure", Description = "Повторных попыток после ошибки")]
    public int MaxRetries { get; set; } = 3;
    [Range(0d, 2d), Display(Name = "Temperature", Description = "Температура")]
    public double Temperature { get; set; } = 0;
    [Range(0.01d, 1d), Display(Name = "Top P", Description = "Top P")]
    public double TopP { get; set; } = 1;
    [Range(0, 2000000), Display(Name = "Max output tokens (0 = server default)", Description = "Макс. токенов ответа (0 = настройка сервера)")]
    public int MaxTokens { get; set; }
    [Range(1, 500), Display(Name = "Model request rounds", Description = "Количество раундов запросов модели")]
    public int MaxRounds { get; set; } = 60;
    [Range(1, 500), Display(Name = "Remind when requests remaining", Description = "Напоминать, когда осталось запросов")]
    public int ReminderRequests { get; set; } = 20;
    [Range(1, 1024), Display(Name = "Tool calls per review", Description = "Вызовов инструментов на коммит")]
    public int MaxToolCalls { get; set; } = 64;
    [Range(16000, 32000000), Display(Name = "Tool results (characters per review)", Description = "Результаты инструментов (символов на коммит)")]
    public int ToolOutputLimit { get; set; } = 512000;
    [Range(1000, 32000000), Display(Name = "Reasoning (characters per response)", Description = "Рассуждения (символов на ответ)")]
    public int ReasoningLimit { get; set; } = 2000000;
    [Range(1000, 32000000), Display(Name = "Content (characters per response)", Description = "Текст ответа (символов на ответ)")]
    public int ContentLimit { get; set; } = 1000000;
    [Range(1000, 8000000), Display(Name = "Tool arguments (characters per response)", Description = "Аргументы инструментов (символов на ответ)")]
    public int ToolArgumentLimit { get; set; } = 256000;
    [Range(1000000, 256000000), Display(Name = "HTTP response (characters)", Description = "HTTP-ответ (символов)")]
    public int TransportLimit { get; set; } = 128000000;
    [Range(0, 1024), Display(Name = "Git snapshots (MiB; 0 = environment/default)", Description = "Git-снимки (МиБ; 0 = окружение/стандарт)")]
    public int SnapshotMb { get; set; }
    public ModelParameters Clone() => (ModelParameters)MemberwiseClone();
    public void Validate()
    {
        if (!double.IsFinite(Temperature) || !double.IsFinite(TopP))
            throw new ValidationException("Temperature and Top P must be finite numbers.");
        Validator.ValidateObject(this, new ValidationContext(this), true);
    }
}

public sealed class ModelsConfiguration
{
    public string ActiveProfile { get; set; } = string.Empty;
    public List<ModelProfile> Profiles { get; } = [];
}
