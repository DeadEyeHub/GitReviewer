using System.Diagnostics;
using System.Net;
using System.Text.Json;
using GitReviewer.Models;
using GitReviewer.Services;

var root = Path.Combine(Path.GetTempPath(), "GitReviewer-availability-" + Guid.NewGuid());
Directory.CreateDirectory(root);
Environment.SetEnvironmentVariable("GITREVIEWER_DATA_DIR", Path.Combine(root, "data"));
var repo = Path.Combine(root, "repo");
Directory.CreateDirectory(repo);
await Git("init", "-b", "main");
await Git("config", "user.name", "Fixture");
await Git("config", "user.email", "fixture@example.invalid");
await File.WriteAllTextAsync(Path.Combine(repo, "file.txt"), "first\n");
await Git("add", ".");
await Git("commit", "-m", "Availability fixture");
var sha = await Git("rev-parse", "HEAD");
var git = new GitService();
var identity = await git.GetRepositoryIdentityAsync(repo, default);
var state = new StateStore();
var profile = new ModelProfile { Model = "fixture", Endpoint = "https://fixture.invalid/v1",
    Parameters = new ModelParameters { MaxRetries = 0, AvailabilityCheckMinutes = 1 } };
var config = new ConfigurationStore();
var models = new ModelsConfiguration { ActiveProfile = profile.Name = "fixture" };
models.Profiles.Add(profile);
config.SaveModels(models);
Check(config.LoadModels().Profiles.Single().Parameters.AvailabilityCheckMinutes == 1, "interval persists");
Check(JsonSerializer.Deserialize<ModelParameters>("{}")!.AvailabilityCheckMinutes == 5, "legacy default");
if (args.Contains("--deadlines"))
{
    await DeadlineChecks.RunAsync(repo, git, config);
    return;
}
var handler = new FixtureHandler();
using var http = new HttpClient(handler);
var runner = new ReviewRunner(git, new ModelClient(http), config, state, new ReportWriter());
var settings = new AppSettings { RepositoryPath = repo, BranchRef = "refs/heads/main", PullEnabled = false, PollIntervalSeconds = 60 };
var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
DateTimeOffset? next = null;
runner.Progress += p => { if (p.Stage == ReviewStage.ModelUnavailable) waiting.TrySetResult(); };
runner.NextRunChanged += value => next = value;
Check(runner.Start(settings, profile), "start");
await waiting.Task.WaitAsync(TimeSpan.FromSeconds(15));
await Task.Delay(100);
Check(next > DateTimeOffset.Now.AddSeconds(50), "configured delay");
await runner.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
Check(await state.GetCursorAsync(identity.CommonGitDirectory, settings.BranchRef, default) is null, "no cursor advance during outage");
Check(!Directory.EnumerateFiles(AppPaths.ReportsDirectory).Any(), "no failed report during outage");
Check(handler.Requests == 1, "no early probe");
Console.WriteLine("PASS: outage preserves commit; Stop cancels waiting; interval persists.");

// Keep the real one-minute delay to exercise the production scheduling path.
waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var completed = new TaskCompletionSource<CommitReviewed>(TaskCreationOptions.RunContinuationsAsynchronously);
runner.Reviewed += item => completed.TrySetResult(item);
Check(runner.Start(settings, profile), "restart");
await waiting.Task.WaitAsync(TimeSpan.FromSeconds(15));
handler.Available = true;
try
{
    var review = await completed.Task.WaitAsync(TimeSpan.FromSeconds(90));
    Check(review.Sha == sha, "same commit resumed");
    Check(handler.Probes == 1, "selected model probed once");
    Check(await state.GetCursorAsync(identity.CommonGitDirectory, settings.BranchRef, default) == sha, "cursor saved after recovery");
    var report = await File.ReadAllTextAsync(Directory.EnumerateFiles(AppPaths.ReportsDirectory).Single());
    Check(!report.Contains("review failed; skipped:"), "outage never recorded as skip");
}
finally { await runner.StopAsync(); }
Console.WriteLine("PASS: model recovers, same commit completes with MaxRetries=0.");

async Task<string> Git(params string[] args)
{
    var start = new ProcessStartInfo("git") { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var arg in args) start.ArgumentList.Add(arg);
    using var process = Process.Start(start)!;
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0) throw new Exception(await error);
    return (await output).Trim();
}
static void Check(bool value, string name) { if (!value) throw new Exception(name); }

sealed class FixtureHandler : HttpMessageHandler
{
    public volatile bool Available;
    public int Requests;
    public int Probes;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Interlocked.Increment(ref Requests);
        if (!Available) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        if (body.RootElement.GetProperty("model").GetString() != "fixture") throw new Exception("Wrong model");
        string json;
        if (!body.RootElement.TryGetProperty("tools", out _))
        {
            Interlocked.Increment(ref Probes);
            json = """{"choices":[{"message":{"content":"OK"},"finish_reason":"stop"}]}""";
        }
        else if (body.RootElement.GetProperty("messages").EnumerateArray().Any(m => m.GetProperty("role").GetString() == "tool"))
            json = """{"choices":[{"message":{"content":"NO_BUGS"},"finish_reason":"stop"}]}""";
        else
            json = """{"choices":[{"message":{"content":null,"tool_calls":[{"id":"diff","type":"function","function":{"name":"git_diff","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}""";
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
    }
}
