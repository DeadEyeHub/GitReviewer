using System.Net;
using System.Text;
using System.Text.Json;
using GitReviewer.Models;
using GitReviewer.Services;

static class DeadlineChecks
{
    public static async Task RunAsync(string repo, GitService git, ConfigurationStore config)
    {
        var profile = new ModelProfile { Name = "deadline", Model = "fixture", Endpoint = "https://fixture.invalid/v1",
            Parameters = new ModelParameters { ReviewMinutes = 2, ResponseInactivityMinutes = 1, MaxRetries = 3 } };
        var models = new ModelsConfiguration { ActiveProfile = profile.Name };
        models.Profiles.Add(profile);
        config.SaveModels(models);
        Check(config.LoadModels().Profiles.Single().Parameters.ResponseInactivityMinutes == 1, "threshold persists");
        Check(JsonSerializer.Deserialize<ModelParameters>("{}")!.ResponseInactivityMinutes == 5, "legacy threshold default");
        foreach (var mode in new[] { "content", "reasoning", "tools", "boundary", "completed", "silent", "stale", "completed-stale", "heartbeat", "cancel" })
        {
            var clock = new ReviewClock();
            using var caller = new CancellationTokenSource();
            using var http = new HttpClient(new DeadlineHandler(clock, mode, caller));
            using var tools = await GitToolSession.CreateAsync(repo, await git.GetHeadAsync(repo, default), default);
            var client = new ModelClient(http, clock);
            Exception? failure = null;
            try { await client.ReviewAsync(profile, tools, "main", "Review", caller.Token); }
            catch (Exception error) { failure = error; }
            var expected = mode switch
            {
                "silent" or "stale" or "completed-stale" or "heartbeat" => typeof(ModelResponseTimeoutException),
                "cancel" => typeof(OperationCanceledException),
                _ => typeof(ReviewComplexityException)
            };
            Check(failure is not null && expected.IsInstanceOfType(failure), $"{mode}: expected {expected.Name}, got {failure}");
            Console.WriteLine($"PASS: deadline classification for {mode}.");
        }

        var settings = new AppSettings { RepositoryPath = repo, BranchRef = "refs/heads/main", PullEnabled = false, PollIntervalSeconds = 60 };
        var activeHandler = new DeadlineHandler(new ReviewClock(), "content");
        using (var http = new HttpClient(activeHandler))
        {
            var state = new StateStore();
            var runner = new ReviewRunner(git, new ModelClient(http, activeHandler.Clock), config, state, new ReportWriter());
            var skipped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            runner.Log += message => { if (message.Contains("failure saved in report")) skipped.TrySetResult(); };
            Check(runner.Start(settings, profile), "automatic review starts");
            try { await skipped.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
            finally { await runner.StopAsync(); }
            var report = await File.ReadAllTextAsync(Directory.EnumerateFiles(AppPaths.ReportsDirectory).Single());
            Check(report.Contains("probably too complex"), "complexity explanation saved in report");
            Check(activeHandler.Reviews == 1 && activeHandler.Probes == 0, "active model is not retried or probed");
            Console.WriteLine("PASS: active deadline writes complexity report without retries.");
        }

        var reportsBefore = await File.ReadAllTextAsync(Directory.EnumerateFiles(AppPaths.ReportsDirectory).Single());
        var silentHandler = new DeadlineHandler(new ReviewClock(), "silent");
        using (var http = new HttpClient(silentHandler))
        using (var cancellation = new CancellationTokenSource())
        {
            var runner = new ReviewRunner(git, new ModelClient(http, silentHandler.Clock), config, new StateStore(), new ReportWriter());
            var review = runner.ReviewSingleCommitAsync(settings, profile, await git.GetHeadAsync(repo, default), cancellation.Token);
            try
            {
                await silentHandler.ProbeStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Check(!review.IsCompleted, "silent review remains pending during probe");
            }
            finally { cancellation.Cancel(); }
            try { await review; throw new Exception("Review must cancel"); }
            catch (OperationCanceledException) { }
            Check(silentHandler.Probes == 1, "silent deadline probes immediately");
            Check(reportsBefore == await File.ReadAllTextAsync(Directory.EnumerateFiles(AppPaths.ReportsDirectory).Single()), "silent deadline produces no failed report");
            Console.WriteLine("PASS: silent deadline probes immediately and remains cancellable, with no failure report.");
        }
    }

    private static void Check(bool value, string name) { if (!value) throw new Exception(name); }
}

sealed class DeadlineHandler(ReviewClock clock, string mode, CancellationTokenSource? caller = null) : HttpMessageHandler
{
    public ReviewClock Clock => clock;
    public int Reviews;
    public int Probes;
    public TaskCompletionSource ProbeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        if (!body.RootElement.TryGetProperty("tools", out _))
        {
            Probes++;
            ProbeStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
        Reviews++;
        if (mode is "completed" or "completed-stale")
        {
            if (Reviews == 1)
            {
                clock.Advance(TimeSpan.FromSeconds(mode == "completed" ? 90 : 30));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                    """{"choices":[{"message":{"content":null,"tool_calls":[{"id":"diff","type":"function","function":{"name":"git_diff","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}""") };
            }
            clock.Advance(TimeSpan.FromMinutes(2) - clock.Elapsed);
            token.ThrowIfCancellationRequested();
        }
        var content = new StreamContent(new DeadlineStream(clock, mode, caller));
        content.Headers.ContentType = new("text/event-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }
}

sealed class DeadlineStream(ReviewClock clock, string mode, CancellationTokenSource? caller) : Stream
{
    private bool _sent;
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        if (!_sent && mode != "silent")
        {
            _sent = true;
            clock.Advance(TimeSpan.FromSeconds(mode == "stale" ? 30 : mode == "boundary" ? 60 : 90));
            var delta = mode switch
            {
                "reasoning" => "\"reasoning_content\":\"Thinking\"",
                "tools" => "\"tool_calls\":[{\"index\":0,\"id\":\"call\",\"type\":\"function\",\"function\":{\"name\":\"git_diff\",\"arguments\":\"{\"}}]",
                _ => "\"content\":\"Investigating\""
            };
            var packet = mode == "heartbeat" ? ": keepalive\n\n" : "data: {\"choices\":[{\"index\":0,\"delta\":{" + delta + "}}]}\n\n";
            var data = Encoding.UTF8.GetBytes(packet);
            data.CopyTo(buffer);
            return ValueTask.FromResult(data.Length);
        }
        if (mode == "cancel") caller!.Cancel();
        clock.Advance(TimeSpan.FromMinutes(2) - clock.Elapsed);
        token.ThrowIfCancellationRequested();
        throw new Exception("Deadline did not cancel the stream");
    }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

// Advance only the model's clock: Git and the operating system retain real time.
sealed class ReviewClock : TimeProvider
{
    private readonly List<ReviewTimer> _timers = [];
    public TimeSpan Elapsed { get; private set; }
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Elapsed.Ticks;
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ReviewTimer(this, callback, state);
        timer.Change(dueTime, period);
        _timers.Add(timer);
        return timer;
    }
    public void Advance(TimeSpan amount)
    {
        Elapsed += amount;
        foreach (var timer in _timers.ToArray()) timer.Fire();
    }
    private sealed class ReviewTimer(ReviewClock clock, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan? _due;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (period != Timeout.InfiniteTimeSpan) throw new NotSupportedException("Only one-shot timers are used");
            _due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.Elapsed + dueTime;
            return true;
        }
        public void Fire() { if (_due <= clock.Elapsed) { _due = null; callback(state); } }
        public void Dispose() => _due = null;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
