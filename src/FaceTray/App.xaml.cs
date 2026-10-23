using System.Threading;
using System.Windows;
using FaceTray.Core;

namespace FaceTray;

/// <summary>
/// App entry: single-instance enforcement, settings load, tray, poller
/// and the ticker window. There is no main window — FaceTray lives in
/// the tray; a second launch signals the running instance to toggle the
/// ticker instead of starting a copy.
/// </summary>
public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\FaceTray.SingleInstance";
    private const string ToggleEventName = @"Local\FaceTray.ToggleTicker";

    private Mutex? _mutex;
    private EventWaitHandle? _toggleEvent;
    private bool _exiting;

    public AppSettings Settings { get; private set; } = new();
    public DiagnosticLog Log { get; private set; } = new(AppPaths.LogPath);
    private LocalReadStore ReadStore { get; set; } = new(AppPaths.ReadStatePath);
    private PollerService? _poller;
    private TrayController? _tray;
    private TickerWindow? _ticker;
    private LoginWindow? _loginWindow;
    private SettingsWindow? _settingsWindow;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Smoke-test mode (used by CI): construct the real ticker window —
        // which runs its XAML — and wait until the poller's WebView2 is
        // initialized, then exit. MuseDesktop v0.1.0 compiled and passed
        // every test yet crashed on first launch because nothing ever built
        // the window; this mode makes CI prove the window constructs AND
        // the WebView2 environment comes up.
        if (e.Args.Any(a => a.Equals("--smoke-test", StringComparison.OrdinalIgnoreCase)))
        {
            await RunSmokeTestAsync();
            return;
        }

        var startInBackground = e.Args.Any(a =>
            a.Equals("--background", StringComparison.OrdinalIgnoreCase));

        _mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirst);
        if (!isFirst)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(ToggleEventName, out var existing))
                {
                    existing.Set();
                    existing.Dispose();
                }
            }
            catch { /* the running instance will simply keep running */ }
            Shutdown();
            return;
        }

        Settings = SettingsStore.Load(AppPaths.SettingsPath);
        Log.Info($"FaceTray {typeof(App).Assembly.GetName().Version} starting" +
                 (startInBackground ? " (background)" : ""));
        ReadStore.Load();

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error($"Unhandled: {args.Exception}");
            args.Handled = true;
            System.Windows.MessageBox.Show(
                "FaceTray hit an unexpected error but kept running where it could.\n\n" +
                $"Details were written to the log:\n{AppPaths.LogPath}",
                "FaceTray", MessageBoxButton.OK, MessageBoxImage.Warning);
        };

        _toggleEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ToggleEventName);
        var listener = new Thread(WaitForToggleSignal) { IsBackground = true, Name = "ToggleSignal" };
        listener.Start();

        Autostart.Sync(Settings.StartWithWindows);

        _poller = new PollerService(Settings, Log, ReadStore, Dispatcher);
        _tray = new TrayController(Log, Dispatcher);
        _ticker = new TickerWindow(_poller);
        Wire();

        _tray.Build();
        _tray.SetPaused(_poller.IsPaused);
        _ = _poller.StartAsync();

        if (!Settings.HasSeenFirstRun)
        {
            Settings.HasSeenFirstRun = true;
            SettingsStore.Save(AppPaths.SettingsPath, Settings);
            // First run: open the ticker so the sign-in state (or, later,
            // the first list) is discoverable instead of a silent tray icon.
            if (!startInBackground)
                _ticker.ShowTicker();
        }
    }

    private void Wire()
    {
        if (_poller is null || _tray is null || _ticker is null)
            return;

        _poller.Changed += () =>
        {
            _tray.SetUnreadCount(_poller.UnreadCount);
            _tray.SetPaused(_poller.IsPaused);
            if (_ticker.IsVisible)
                _ticker.Refresh();
        };
        _poller.NewUnreadArrived += newCount =>
        {
            if (Settings.ShowToasts)
                _tray.ShowNewNotificationsBalloon(newCount);
        };

        _tray.ToggleTickerRequested = () => _ticker.ToggleTicker();
        _tray.OpenFacebookRequested = () => BrowserOpener.Open(UrlGuard.FacebookHomeUrl, Log);
        _tray.OpenNotificationsRequested = () => BrowserOpener.Open(UrlGuard.NotificationsUrl, Log);
        _tray.RefreshRequested = () => _ = _poller.RefreshNowAsync();
        _tray.MarkAllReadRequested = () => _poller.MarkAllRead();
        _tray.TogglePauseRequested = () => _poller.SetPaused(!_poller.IsPaused);
        _tray.SettingsRequested = ShowSettings;
        _tray.ExitRequested = ExitApp;

        _ticker.SignInRequested = ShowLogin;
        _ticker.RefreshRequested = () => _ = _poller.RefreshNowAsync();
        _ticker.MarkAllReadRequested = () => _poller.MarkAllRead();
        _ticker.SettingsRequested = ShowSettings;
        _ticker.TogglePauseRequested = () => _poller.SetPaused(!_poller.IsPaused);
        _ticker.ExitRequested = ExitApp;
        _ticker.OpenNotificationsPageRequested = url => BrowserOpener.Open(url, Log);
        _ticker.NotificationClicked = item =>
        {
            _poller.MarkRead(item.Id);
            _ticker.Hide();
            BrowserOpener.Open(item.Href, Log);
        };
    }

    private void ShowLogin()
    {
        if (_loginWindow is not null)
        {
            _loginWindow.Activate();
            return;
        }
        _loginWindow = new LoginWindow(Log);
        _loginWindow.SignInFinished += () =>
        {
            _loginWindow = null;
            if (!_exiting)
                _ = _poller?.RefreshNowAsync();
        };
        _loginWindow.Show();
    }

    private void ShowSettings()
    {
        _ticker?.Hide();
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(Settings, Log);
        _settingsWindow.Saved += saved =>
        {
            Settings = saved;
            _poller?.ApplySettings();
        };
        _settingsWindow.ClearDataRequested += () =>
        {
            ReadStore.Clear();
            NotificationCacheStore.Clear(AppPaths.CachePath);
            _poller?.ClearLocalState();
            _tray?.SetUnreadCount(0);
        };
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private void ExitApp()
    {
        if (_exiting)
            return;
        _exiting = true;
        Log.Info("FaceTray exiting");
        try
        {
            _loginWindow?.Close();
            _settingsWindow?.Close();
            _ticker?.Close();
            _poller?.Dispose();
            // Tray disposal happens here — on the app's single exit path,
            // never inside a tray event handler's own dispatch.
            _tray?.Dispose();
        }
        catch { /* shutdown must not hang on cleanup */ }
        Shutdown();
    }

    private async Task RunSmokeTestAsync()
    {
        Settings = SettingsStore.Load(AppPaths.SettingsPath);
        ReadStore.Load();
        PollerService poller;
        TickerWindow ticker;
        try
        {
            poller = new PollerService(Settings, Log, ReadStore, Dispatcher);
            ticker = new TickerWindow(poller);
            ticker.ShowTicker();
        }
        catch (Exception ex)
        {
            Log.Error($"SMOKE-TEST FAILED (window construction): {ex}");
            Shutdown(1);
            return;
        }

        Log.Info("SMOKE-TEST: ticker window constructed");
        var startTask = poller.StartAsync();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        while (DateTime.UtcNow < deadline && !poller.WebReady && poller.State != PollerState.Error)
            await Task.Delay(250);
        await startTask;

        if (poller.WebReady)
        {
            Log.Info($"SMOKE-TEST OK: ticker constructed and WebView2 initialized (state {poller.State})");
            ticker.Close();
            poller.Dispose();
            Shutdown(0);
        }
        else
        {
            Log.Error($"SMOKE-TEST FAILED: WebView2 did not initialize within 45s (state {poller.State}: {poller.LastError})");
            Shutdown(1);
        }
    }

    private void WaitForToggleSignal()
    {
        while (_toggleEvent is not null && !_exiting)
        {
            try
            {
                _toggleEvent.WaitOne();
                Dispatcher.BeginInvoke(() => _ticker?.ToggleTicker());
            }
            catch (ObjectDisposedException) { return; }
            catch (ThreadInterruptedException) { return; }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _toggleEvent?.Dispose();
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
        }
        catch { /* shutdown must not hang on cleanup */ }
        base.OnExit(e);
    }
}
