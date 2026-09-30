using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitReviewer.Models;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace GitReviewer.Services;

public sealed class MailSettings
{
    public bool Enabled { get; set; }
    public bool SendToAuthor { get; set; }
    public bool SendWithoutBugs { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string Security { get; set; } = "StartTls";
    public string Username { get; set; } = "";
    public string EncryptedPassword { get; set; } = "";
    public string From { get; set; } = "";
    public string Recipients { get; set; } = "";
    public MailSettings Clone() => (MailSettings)MemberwiseClone();
    public void SetPassword(string password) => EncryptedPassword = password.Length == 0 ? "" :
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser));
    public string GetPassword() => EncryptedPassword.Length == 0 ? "" :
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(EncryptedPassword), null, DataProtectionScope.CurrentUser));
    public List<MailboxAddress> Addresses() => Recipients.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(MailboxAddress.Parse).ToList();
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host) || Host.Any(char.IsWhiteSpace) || Host.Contains('/') || Port is < 1 or > 65535)
            throw new InvalidOperationException(Localization.Text("Invalid SMTP host or port.", "Некорректный SMTP-сервер или порт."));
        if (Security is not ("None" or "StartTls" or "SslOnConnect")) throw new InvalidOperationException("Invalid SMTP security mode.");
        if (From.Any(char.IsControl) || Recipients.Any(char.IsControl) || Username.Any(char.IsControl))
            throw new InvalidOperationException("Invalid mail address or username.");
        _ = MailboxAddress.Parse(From);
        if (Addresses().Count == 0 && !SendToAuthor) throw new InvalidOperationException(Localization.Text("Enter a recipient.", "Укажите получателя."));
        _ = GetPassword();
    }
}

public sealed class MailJob
{
    public string Key { get; set; } = "";
    public string MessageId { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Report { get; set; } = "";
    public string From { get; set; } = "";
    public string Recipients { get; set; } = "";
    public int Attempts { get; set; }
    public bool Sent { get; set; }
    public DateTimeOffset NextAttempt { get; set; }
}

public sealed class MailNotifications : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly string _settingsPath, _queuePath;
    private readonly Action<string> _log;
    private readonly Func<MailSettings, MailJob, CancellationToken, Task> _send;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _sending = new(1, 1);
    private readonly List<MailJob> _jobs;
    private readonly Task _worker;
    private MailSettings _settings;
    private readonly Func<DateTimeOffset> _now;
    public string? LoadError { get; }
    public MailSettings Settings { get { lock (_gate) return _settings.Clone(); } }

    public MailNotifications(string directory, Action<string> log,
        Func<MailSettings, MailJob, CancellationToken, Task>? send = null, bool startWorker = true, Func<DateTimeOffset>? now = null)
    {
        _settingsPath = Path.Combine(directory, "mail-settings.json");
        _queuePath = Path.Combine(directory, "mail-queue.json");
        // A UI/logging failure must never terminate the delivery worker.
        _log = message => { try { log(message); } catch { } };
        _send = send ?? SendSmtpAsync;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        try
        {
            _settings = File.Exists(_settingsPath) ? JsonSerializer.Deserialize<MailSettings>(File.ReadAllText(_settingsPath))
                ?? throw new InvalidDataException() : new();
            _jobs = File.Exists(_queuePath) ? JsonSerializer.Deserialize<List<MailJob>>(File.ReadAllText(_queuePath))
                ?? throw new InvalidDataException() : [];
            if (_jobs.Any(j => j is null || string.IsNullOrWhiteSpace(j.Key) || j.Attempts < 0)) throw new InvalidDataException();
        }
        catch (Exception e)
        {
            _settings = new(); _jobs = [];
            LoadError = "Mail settings/queue could not be loaded (" + e.GetType().Name + "). Existing files are preserved.";
        }
        _worker = startWorker ? Task.Run(WorkerAsync) : Task.CompletedTask;
    }

    private static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var file = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, value); file.Flush(true); }
        File.Move(path + ".tmp", path, true);
    }
    public void SaveSettings(MailSettings settings)
    {
        if (settings.Enabled) settings.Validate();
        lock (_gate)
        {
            if (LoadError is not null) throw new InvalidOperationException(LoadError);
            Save(_settingsPath, settings);
            _settings = settings.Clone();
        }
    }
    public void Enqueue(CommitReviewed review)
    {
        if (review.HasUnstructuredResponse) { _log("Mail: incomplete review skipped: " + review.Sha); return; }
        lock (_gate)
        {
            if (LoadError is not null) { _log(LoadError); return; }
            if (!_settings.Enabled) { _log("Mail: notifications disabled; skipped: " + review.Sha); return; }
            if ((review.FindingCount == 0 || review.EmptyDiff) && !_settings.SendWithoutBugs)
            { _log("Mail: no findings; clean-review notifications disabled; skipped: " + review.Sha); return; }
            try
            {
                _settings.Validate();
                var recipients = _settings.Addresses();
                if (_settings.SendToAuthor && !string.IsNullOrWhiteSpace(review.AuthorEmail))
                {
                    // Commit metadata is untrusted: accept exactly one bare mailbox, never a header/list.
                    var email = review.AuthorEmail.Trim();
                    if (!email.Any(char.IsControl) && MailboxAddress.TryParse(email, out var author) &&
                        author.Address.Equals(email, StringComparison.OrdinalIgnoreCase) && email.Contains('@'))
                    {
                        if (!recipients.Any(r => r.Address.Equals(author.Address, StringComparison.OrdinalIgnoreCase))) recipients.Add(author);
                    }
                    else _log("Mail: invalid commit author address skipped.");
                }
                if (recipients.Count == 0) { _log("Mail: no valid recipients for this commit; skipped."); return; }
                if (string.IsNullOrWhiteSpace(review.ReportMarkdown)) throw new InvalidDataException("Report is unavailable.");
                var identity = Path.GetFullPath(string.IsNullOrEmpty(review.RepositoryIdentity) ? review.RepositoryPath : review.RepositoryIdentity);
                if (OperatingSystem.IsWindows()) identity = identity.ToUpperInvariant();
                var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity + "\n" + review.BranchRef + "\n" + review.Sha)));
                if (_jobs.Any(j => j.Key == key)) { _log("Mail: this commit is already queued or sent: " + review.Sha); return; }
                var job = new MailJob { Key = key, MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId(),
                    Subject = Clean($"[GitReviewer] {Path.GetFileName(review.RepositoryPath)} / {review.BranchRef} / {review.Sha[..Math.Min(8, review.Sha.Length)]} — bugs: {review.FindingCount}"),
                    Report = review.ReportMarkdown, From = _settings.From, Recipients = string.Join(";", recipients.Select(r => r.ToString())), NextAttempt = _now() };
                _jobs.Add(job);
                try { Save(_queuePath, _jobs); } catch { _jobs.Remove(job); throw; }
                _log(Localization.Text("Mail: report queued: ", "Почта: отчёт поставлен в очередь: ") + job.Subject);
            }
            catch (Exception e) { _log("Mail: cannot queue report (" + e.GetType().Name + "). Review is saved."); }
        }
    }
    private static string Clean(string text) => new(text.Where(c => !char.IsControl(c)).Take(250).ToArray());

    public async Task TestAsync(MailSettings settings, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
        token = linked.Token;
        settings.Validate();
        if (settings.Addresses().Count == 0) throw new InvalidOperationException("Enter an explicit recipient for the test email.");
        SaveSettings(settings);
        await _sending.WaitAsync(token);
        try
        {
            await _send(settings, new MailJob { MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId(),
                Subject = "GitReviewer SMTP test", Report = "SMTP test message from GitReviewer. No repository data is included.",
                From = settings.From, Recipients = settings.Recipients }, token);
        }
        finally { _sending.Release(); }
    }
    public void RetryFailed()
    {
        lock (_gate)
        {
            if (LoadError is not null) throw new InvalidOperationException(LoadError);
            foreach (var job in _jobs.Where(j => !j.Sent && j.Attempts >= 4))
            { job.Attempts = 0; job.NextAttempt = _now(); }
            Save(_queuePath, _jobs);
        }
    }
    public (int Pending, int Failed) Counts()
    { lock (_gate) return (_jobs.Count(j => !j.Sent && j.Attempts < 4), _jobs.Count(j => !j.Sent && j.Attempts >= 4)); }

    private async Task WorkerAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try { await ProcessOnceAsync(_stop.Token); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception e) { _log("Mail queue error (" + e.GetType().Name + ")."); }
            try { await Task.Delay(2000, _stop.Token); } catch (OperationCanceledException) { break; }
        }
    }
    public async Task ProcessOnceAsync(CancellationToken token)
    {
        await _sending.WaitAsync(token);
        try
        {
            MailJob? job;
            MailSettings settings;
            lock (_gate)
            {
                if (LoadError is not null || !_settings.Enabled) return;
                job = _jobs.FirstOrDefault(j => !j.Sent && j.Attempts < 4 && j.NextAttempt <= _now());
                if (job is null) return;
                settings = _settings.Clone();
                job.Attempts++;
                job.NextAttempt = _now().AddMinutes(job.Attempts switch { 1 => 1, 2 => 5, _ => 15 });
                Save(_queuePath, _jobs); // Persist attempts before touching the network.
            }
            try
            {
                await _send(settings, job, token);
                lock (_gate) { job.Sent = true; job.Report = ""; Save(_queuePath, _jobs); }
                _log(Localization.Text("Mail: report accepted by SMTP server: ", "Почта: отчёт принят SMTP-сервером: ") + job.Subject);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception e)
            {
                // Never include server text or credentials in logs.
                _log($"Mail: attempt {job.Attempts}/4 failed ({e.GetType().Name}). " +
                    (job.Attempts >= 4 ? "Use Retry failed mail." : "Retry scheduled."));
            }
        }
        finally { _sending.Release(); }
    }

    public static async Task SendSmtpAsync(MailSettings settings, MailJob job, CancellationToken token)
    {
        settings.Validate();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var client = new SmtpClient { Timeout = 30000 };
        await client.ConnectAsync(settings.Host, settings.Port, settings.Security switch
        { "None" => SecureSocketOptions.None, "SslOnConnect" => SecureSocketOptions.SslOnConnect, _ => SecureSocketOptions.StartTls }, timeout.Token);
        if (!string.IsNullOrWhiteSpace(settings.Username))
            await client.AuthenticateAsync(settings.Username, settings.GetPassword(), timeout.Token);
        using var message = CreateMessage(job);
        await client.SendAsync(message, timeout.Token);
        // Once accepted, a disconnect failure must not schedule a duplicate.
        try { await client.DisconnectAsync(true, timeout.Token); } catch { }
    }

    public static MimeMessage CreateMessage(MailJob job)
    {
        var message = new MimeMessage { Subject = job.Subject, MessageId = job.MessageId, Date = DateTimeOffset.Now };
        message.From.Add(MailboxAddress.Parse(job.From));
        foreach (var address in job.Recipients.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            message.To.Add(MailboxAddress.Parse(address));
        var html = RenderHtml(job.Report);
        var body = new BodyBuilder { TextBody = job.Report, HtmlBody = html };
        body.Attachments.Add("commit-review.html", Encoding.UTF8.GetBytes(html), new ContentType("text", "html"));
        message.Body = body.ToMessageBody();
        return message;
    }

    public static string RenderHtml(string report)
    {
        var html = new StringBuilder("<!doctype html><html><head><meta charset=\"utf-8\"></head><body style=\"font-family:Segoe UI,Arial,sans-serif;color:#202124;line-height:1.5;max-width:960px;margin:24px\">");
        var fenced = false;
        foreach (var raw in report.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.StartsWith("```", StringComparison.Ordinal))
            {
                html.Append(fenced ? "</code></pre>" : "<pre style=\"white-space:pre-wrap;background:#f3f5f7;padding:12px\"><code>");
                fenced = !fenced;
                continue;
            }
            if (!fenced && raw.StartsWith("<!--", StringComparison.Ordinal)) continue;
            var encoded = System.Net.WebUtility.HtmlEncode(raw);
            if (fenced) { html.Append(encoded).Append('\n'); continue; }
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var level = raw.TakeWhile(c => c == '#').Count();
            if (level is >= 1 and <= 3 && raw.Length > level && raw[level] == ' ')
                html.Append($"<h{level}>").Append(System.Net.WebUtility.HtmlEncode(raw[(level + 1)..].Replace("`", ""))).Append($"</h{level}>");
            else html.Append("<p style=\"margin:6px 0;white-space:pre-wrap\">").Append(encoded).Append("</p>");
        }
        if (fenced) html.Append("</code></pre>");
        return html.Append("</body></html>").ToString();
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _worker;
        await _sending.WaitAsync();
        _sending.Release();
        _stop.Dispose();
    }
}
