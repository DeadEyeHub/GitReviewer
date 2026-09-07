using System.Threading;
using System.Windows;

namespace GitReviewer;

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\GitReviewer.SingleInstance";
    private const string ShowEventName = @"Local\GitReviewer.ShowWindow";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private CancellationTokenSource? _showListenerCancellation;

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
