using System.Threading;
using System.Windows;
using GitReviewer.Services;

namespace GitReviewer;

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\GitReviewer.SingleInstance";
    private const string ShowEventName = @"Local\GitReviewer.ShowWindow";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private CancellationTokenSource? _showListenerCancellation;
    private int _reportingFatal;

    public App()
    {
        DispatcherUnhandledException += (_, e) => ReportFatal(e.Exception, "WPF dispatcher");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => SaveFailure(
            e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()),
            $"AppDomain (terminating={e.IsTerminating})");
        TaskScheduler.UnobservedTaskException += (_, e) => SaveFailure(e.Exception, "Unobserved task");
        System.Windows.Forms.Application.ThreadException += (_, e) =>
        {
            ReportFatal(e.Exception, "Windows Forms thread");
            Environment.Exit(1);
        };
    }

    private static string? SaveFailure(Exception exception, string source)
    {
        string directory;
        try { directory = System.IO.Path.Combine(AppPaths.DataDirectory, "crashes"); }
        catch { directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GitReviewer-crashes"); }
        return CrashDiagnostics.Save(exception, source, directory);
    }

    private void ReportFatal(Exception exception, string source)
    {
        var path = SaveFailure(exception, source);
        if (Interlocked.Exchange(ref _reportingFatal, 1) != 0) return;
        try
        {
            System.Windows.MessageBox.Show(
                "GitReviewer: необработанная ошибка. Приложение будет закрыто.\n\n" +
                exception.GetType().Name + ": " + exception.Message + "\n\n" +
                (path is null ? "Не удалось сохранить аварийный отчёт." : "Аварийный отчёт: " + path),
                "GitReviewer — crash", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }
        // Do not mark dispatcher exceptions handled: continuing could advance review state unsafely.
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _mutex = new Mutex(true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            _showEvent.Set();
            Shutdown();
            return;
        }

        _showListenerCancellation = new CancellationTokenSource();

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        _ = ListenForShowRequestsAsync(window, _showListenerCancellation.Token);
    }

    private async Task ListenForShowRequestsAsync(MainWindow window, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Run(() => _showEvent!.WaitOne(), cancellationToken).ConfigureAwait(false);
                if (!cancellationToken.IsCancellationRequested)
                    await Dispatcher.InvokeAsync(window.ShowFromTray);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showListenerCancellation?.Cancel();
        _showEvent?.Set();
        _showEvent?.Dispose();
        _showListenerCancellation?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
