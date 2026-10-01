namespace GitReviewer.Models;

public sealed class ProjectCatalog
{
    public int Version { get; set; } = 1;
    public int ActiveIndex { get; set; }
    public List<AppSettings> Projects { get; set; } = [];
}

public sealed class AppSettings
{
    public string ProjectId { get; set; } = "";
    public string RepositoryPath { get; set; } = string.Empty;
    public string BranchRef { get; set; } = string.Empty;
    public int PollIntervalSeconds { get; set; } = 60;
    public bool PullEnabled { get; set; } = true;
    public bool FetchSubmodules { get; set; }
    public bool UpdateSubmodulesAfterAdvance { get; set; } = true;
    public bool QuietHoursEnabled { get; set; }
    public string QuietHoursStart { get; set; } = "09:00";
    public string QuietHoursEnd { get; set; } = "18:00";
    public string Language { get; set; } = "en";
    public string GitAuthenticationMode { get; set; } = "auto";
    public string SshPrivateKeyPath { get; set; } = string.Empty;
    public string PlinkPath { get; set; } = string.Empty;
}

public sealed class DailyModelSchedule
{
    private readonly bool _enabled;
    private readonly TimeOnly _start, _end;
    public DailyModelSchedule(AppSettings settings)
    {
        _enabled = settings.QuietHoursEnabled;
        if (!_enabled) return;
        if (!TimeOnly.TryParseExact(settings.QuietHoursStart, "HH:mm", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out _start) ||
            !TimeOnly.TryParseExact(settings.QuietHoursEnd, "HH:mm", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out _end) || _start == _end)
            throw new ArgumentException("Quiet hours require different start/end times in HH:mm format (local computer time).");
    }
    public bool IsBlocked(DateTime localNow)
    {
        var time = TimeOnly.FromDateTime(localNow);
        return _enabled && (_start < _end ? time >= _start && time < _end : time >= _start || time < _end);
    }
    public DateTime ResumeAt(DateTime localNow) => !IsBlocked(localNow) ? localNow :
        localNow.Date.AddDays(_start > _end && TimeOnly.FromDateTime(localNow) >= _start ? 1 : 0).Add(_end.ToTimeSpan());
    public void Check(DateTime localNow)
    {
        if (IsBlocked(localNow)) throw new ModelSchedulePauseException($"Model paused by schedule until {ResumeAt(localNow):yyyy-MM-dd HH:mm} (local time). The commit was not skipped.");
    }
}

public sealed class ModelSchedulePauseException(string message) : Exception(message);
