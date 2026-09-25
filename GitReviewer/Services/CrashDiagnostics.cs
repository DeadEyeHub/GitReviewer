using System.Reflection;
using System.Text;

namespace GitReviewer.Services;

public static class CrashDiagnostics
{
    public static string Context { get; set; } = "No commit selected";

    // Independent of UI logging: usable during startup, dispatcher failures and shutdown.
    public static string? Save(Exception exception, string source, string directory, string? fallbackDirectory = null)
    {
        try
        {
            var report = $"Time: {DateTimeOffset.Now:O}\nSource: {source}\n" +
                $"Version: {Assembly.GetExecutingAssembly().GetName().Version}\nRuntime: {Environment.Version}\n" +
                $"OS: {Environment.OSVersion}\nThread: {Environment.CurrentManagedThreadId}\nContext: {Context}\n\n{exception}\n";
            foreach (var target in new[] { directory, fallbackDirectory ?? Path.Combine(Path.GetTempPath(), "GitReviewer-crashes") })
            {
                try
                {
                    Directory.CreateDirectory(target);
                    var path = Path.Combine(target, $"crash-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.log");
                    using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    file.Write(Encoding.UTF8.GetBytes(report));
                    file.Flush(true);
                    return path;
                }
                catch { /* Try fallback without throwing from the exception handler. */ }
            }
        }
        catch { /* Severe failures can prevent diagnostic allocation or formatting. */ }
        return null;
    }
}
