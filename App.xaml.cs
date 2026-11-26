using System.Windows;
using TapRoom.Services;

namespace TapRoom;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private SingleInstanceCoordinator? _singleInstanceCoordinator;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!SingleInstanceCoordinator.TryAcquire(out _singleInstanceMutex))
        {
            SingleInstanceCoordinator.TryForwardArguments(e.Args);
            Shutdown();
            return;
        }

        // Keep the app alive while early startup windows, such as --prompt-url, are open.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        MainWindow? mainWindow = null;
        var pendingExternalArgs = new List<string[]>();
        var pendingLock = new object();

        void HandleExternalArgs(string[] args)
        {
            if (mainWindow is null)
            {
                lock (pendingLock)
                    pendingExternalArgs.Add(args);
                return;
            }

            mainWindow.Dispatcher.BeginInvoke(async () => await mainWindow.HandleExternalStartupArgsAsync(args));
        }

        _singleInstanceCoordinator = new SingleInstanceCoordinator(HandleExternalArgs);
        _singleInstanceCoordinator.Start();

        var options = StartupOptions.Parse(e.Args);
        if (options.PromptForUrl)
        {
            var prompt = new UrlPromptWindow();
            if (prompt.ShowDialog() != true)
            {
                Shutdown();
                return;
            }

            options = options with { Url = prompt.Url };
        }

        mainWindow = new MainWindow(options);
        MainWindow = mainWindow;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        mainWindow.Show();

        List<string[]> queued;
        lock (pendingLock)
        {
            queued = pendingExternalArgs.ToList();
            pendingExternalArgs.Clear();
        }

        foreach (var args in queued)
            HandleExternalArgs(args);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceCoordinator?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
