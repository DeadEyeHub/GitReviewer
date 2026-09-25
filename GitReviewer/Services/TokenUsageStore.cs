using System.Text.Json;

namespace GitReviewer.Services;

public sealed record TokenUsage(long Input, long Output, long Total)
{
    public static TokenUsage? Parse(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;
        long? Read(string name) => usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt64(out var number) && number >= 0 ? number : null;
        var input = Read("prompt_tokens");
        var output = Read("completion_tokens");
        var total = Read("total_tokens");
        if (total is null && (input is null || output is null || input > long.MaxValue - output)) return null;
        return new(input ?? 0, output ?? 0, total ?? (input!.Value + output!.Value));
    }
}

public sealed class TokenTally
{
    public long Tokens { get; set; }
    public long Requests { get; set; }
    public long MissingUsage { get; set; }
    public void Add(TokenUsage? usage)
    {
        Requests = checked(Requests + 1);
        if (usage is null) MissingUsage = checked(MissingUsage + 1);
        else Tokens = checked(Tokens + usage.Total);
    }
    public TokenTally Copy() => new() { Tokens = Tokens, Requests = Requests, MissingUsage = MissingUsage };
}

public sealed class TokenUsageStore
{
    public sealed class Data
    {
        public TokenTally Total { get; set; } = new();
        public Dictionary<string, TokenTally> Days { get; set; } = new();
        public Dictionary<string, TokenTally> Commits { get; set; } = new();
    }
    private readonly object _gate = new();
    private readonly string _path;
    private Data _data = new();
    private bool _loadFailed;
    public string? Error { get; private set; }
    public TokenUsageStore(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path)) _data = JsonSerializer.Deserialize<Data>(File.ReadAllText(path)) ?? throw new InvalidDataException("Empty usage file");
            if (_data.Total is null || _data.Days is null || _data.Commits is null ||
                _data.Days.Values.Concat(_data.Commits.Values).Append(_data.Total).Any(t => t is null || t.Tokens < 0 || t.Requests < 0 || t.MissingUsage < 0))
                throw new InvalidDataException("Invalid usage counters");
        }
        catch (Exception e) { _loadFailed = true; _data = new(); Error = "Token usage could not be loaded: " + e.Message; }
    }
    public static string Key(string repository, string sha) =>
        (OperatingSystem.IsWindows() ? Path.GetFullPath(repository).ToUpperInvariant() : Path.GetFullPath(repository)).TrimEnd(Path.DirectorySeparatorChar) + "|" + sha;
    public void Record(string repository, string sha, TokenUsage? usage, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_loadFailed) return; // Never overwrite unreadable historical totals with zeros.
            try
            {
                var day = now.ToString("yyyy-MM-dd");
                var key = Key(repository, sha);
                var total = _data.Total.Copy();
                var daily = _data.Days.GetValueOrDefault(day)?.Copy() ?? new();
                var commit = _data.Commits.GetValueOrDefault(key)?.Copy() ?? new();
                total.Add(usage); daily.Add(usage); commit.Add(usage);
                _data.Total = total; _data.Days[day] = daily; _data.Commits[key] = commit;
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var temp = _path + ".tmp";
                using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(file, _data);
                    file.Flush(true);
                }
                File.Move(temp, _path, true);
                Error = null;
            }
            catch (Exception e) { Error = "Token usage could not be saved: " + e.Message; }
        }
    }
    public (TokenTally Commit, TokenTally Today, TokenTally Total) Snapshot(string? key, DateTimeOffset now)
    {
        lock (_gate) return (key is not null && _data.Commits.TryGetValue(key, out var commit) ? commit.Copy() : new(),
            _data.Days.GetValueOrDefault(now.ToString("yyyy-MM-dd"))?.Copy() ?? new(), _data.Total.Copy());
    }
}
