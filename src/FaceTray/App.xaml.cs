using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Media;
using System.Net.Http;
using System.Threading;
using System.Windows;
using FaceTray.Core;

namespace FaceTray;

/// <summary>
/// App entry: single-instance enforcement, settings load, tray, poller
/// and the ticker window. There is no main window — FaceTray lives in
/// the tray; a second launch signals the running instance to toggle the
/// ticker instead of starting a copy.
///
/// v0.2.0: everything account-scoped (read store, snooze store, cache
/// file, WebView2 session folder) is built for the ACTIVE profile at
/// startup; switching profiles saves and relaunches into the new one.
/// </summary>
public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\FaceTray.SingleInstance";
    private const string ToggleEventName = @"Local\FaceTray.ToggleTicker";
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/SeanShaffer88/FaceTray/releases/latest";
    private const string ReleasePagePrefix = "https://github.com/SeanShaffer88/FaceTray/";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private Mutex? _mutex;
    private EventWaitHandle? _toggleEvent;
    private bool _exiting;

    public AppSettings Settings { get; private set; } = new();
    public DiagnosticLog Log { get; private set; } = new(AppPaths.LogPath);

    private LocalReadStore _readStore = new(AppPaths.ReadStatePath);
    private SnoozeStore _snoozeStore = new(AppPaths.SnoozePath);
    private PinStore _pinStore = new(AppPaths.PinPath);
    private AlertTrackStore _alertTrackStore = new(AppPaths.AlertTrackPath);
    private string _cachePath = AppPaths.CachePath;
    private string _sessionDir = AppPaths.WebViewDataDir;
    private string _lastGateState = "";

    private PollerService? _poller;
    private TrayController? _tray;
    private TickerWindow? _ticker;
    private ToastWindow? _toast;
    private HotkeyManager? _hotkey;
    private LoginWindow? _loginWindow;
    private SettingsWindow? _settingsWindow;
    private UpdateInfo? _availableUpdate;

    private static string AppVersion =>
        typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

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
        Log.Info($"FaceTray {AppVersion} starting" +
                 (startInBackground ? " (background)" : "") +
                 $"; profile {Settings.ActiveProfileId}");

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
        ProcessPendingProfileDeletions();
        BuildProfileContext();

        _poller = new PollerService(Settings, Log, _readStore, _snoozeStore, _pinStore, _alertTrackStore, _cachePath, _sessionDir, Dispatcher);
        _tray = new TrayController(Log, Dispatcher);
        _ticker = new TickerWindow(_poller, Log) { DisplaySettings = Settings };
        _hotkey = new HotkeyManager(Log);
        _hotkey.Pressed += () => _ticker?.ToggleTicker();
        Wire();

        _tray.Build();
        _tray.SetAccentColor(Settings.ActiveProfile.AccentColor);
        _tray.SetPaused(_poller.IsPaused);
        RefreshTrayProfileLabel();
        SyncHotkey();
        _ = _poller.StartAsync();
        if (Settings.CheckForUpdates)
            _ = CheckForUpdateAsync();

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

    /// <summary>
    /// Builds the active profile's stores and prunes them per the history
    /// setting: expired snoozes out, read marks older than the keep window
    /// out (the cache prunes itself on load, inside the poller).
    /// </summary>
    private void BuildProfileContext()
    {
        var id = Settings.ActiveProfileId;
        _cachePath = AppPaths.CachePathFor(id);
        _sessionDir = AppPaths.WebViewDataDirFor(id);

        _readStore = new LocalReadStore(AppPaths.ReadStatePathFor(id));
        _readStore.Load();
        _snoozeStore = new SnoozeStore(AppPaths.SnoozePathFor(id));
        _snoozeStore.Load();
        _pinStore = new PinStore(AppPaths.PinPathFor(id));
        _pinStore.Load();
        _alertTrackStore = new AlertTrackStore(AppPaths.AlertTrackPathFor(id));
        _alertTrackStore.Load();

        var now = DateTimeOffset.UtcNow;
        if (_snoozeStore.PruneExpired(now) > 0)
            _snoozeStore.Save();
        if (_readStore.PruneOlderThan(now.AddDays(-Settings.HistoryKeepDays)) > 0)
            _readStore.Save();
        // Re-alert tracking is pruned with the history window (pins are
        // deliberately NOT pruned — they are the user's own keepsakes).
        if (_alertTrackStore.PruneOlderThan(now.AddDays(-Settings.HistoryKeepDays)) > 0)
            _alertTrackStore.Save();
    }

    private void Wire()
    {
        if (_poller is null || _tray is null || _ticker is null)
            return;

        _poller.Changed += () =>
        {
            _tray.SetUnreadCount(_poller.UnreadCount);
            _tray.SetPaused(_poller.IsPaused);
            _tray.SetRecentItems(_poller.Items.Take(5).ToList());
            _tray.RefreshMenu();
            if (_ticker.IsVisible)
                _ticker.Refresh();
        };
        _poller.NewItemsArrived += newItems => OnNewItems(newItems);
        _poller.RealertReady += item =>
        {
            // The poller already applied the toast decision (type rules +
            // quiet hours); compose the shell's Focus Assist state here.
            if (FocusAssistAllows())
            {
                _toast ??= CreateToast();
                _toast.ShowNotification(item, 0);
                PlaySoundIfEnabled();
                Log.Info("Re-alerted one still-unread comment/mention notification");
            }
            _poller.NoteRealertDelivered(item.Id);
        };
        _poller.DigestReady += (count, newest) =>
        {
            Settings.LastDigestDate = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            SettingsStore.Save(AppPaths.SettingsPath, Settings);
            _poller.NoteDigestSent();
            Log.Info($"Morning digest due: {count} new since the last digest");
            if (newest is not null && Settings.ShowToasts && FocusAssistAllows())
            {
                _toast ??= CreateToast();
                _toast.ShowDigest(newest, count);
                PlaySoundIfEnabled();
            }
        };

        _tray.ToggleTickerRequested = () => _ticker.ToggleTicker();
        _tray.OpenFacebookRequested = () => BrowserOpener.Open(UrlGuard.FacebookHomeUrl, Log);
        _tray.OpenNotificationsRequested = () => BrowserOpener.Open(UrlGuard.NotificationsUrl, Log);
        _tray.RefreshRequested = () => _ = _poller.RefreshNowAsync();
        _tray.MarkAllReadRequested = MarkAllReadWithUndo;
        _tray.TogglePauseRequested = () => _poller.SetPaused(!_poller.IsPaused);
        _tray.SettingsRequested = ShowSettings;
        _tray.ExitRequested = ExitApp;
        _tray.RecentItemClicked = item =>
        {
            _poller.MarkRead(item.Id);
            BrowserOpener.Open(item.Href, Log);
        };
        _tray.UpdateClicked = () =>
        {
            if (_availableUpdate is not null)
                OpenReleasePage(_availableUpdate.Url);
        };

        _ticker.SignInRequested = ShowLogin;
        _ticker.RefreshRequested = () => _ = _poller.RefreshNowAsync();
        _ticker.MarkAllReadRequested = MarkAllReadWithUndo;
        _ticker.UndoMarkAllReadRequested = undo => _poller.UndoMarkAllRead(undo);
        _ticker.PinToggleRequested = item => _poller.SetPinned(item.Id, !item.IsPinned);
        _ticker.OpenAllUnreadRequested = items =>
        {
            foreach (var item in items)
            {
                _poller.MarkRead(item.Id);
                BrowserOpener.Open(item.Href, Log);
            }
        };
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
        _ticker.MarkReadRequested = item => _poller.MarkRead(item.Id);
        _ticker.SnoozeRequested = (item, option) =>
        {
            var now = DateTimeOffset.Now;
            var until = option == SnoozeOption.OneHour
                ? SnoozeTiming.OneHourFrom(now)
                : SnoozeTiming.UntilTomorrowMorning(now);
            _poller.Snooze(item.Id, until);
        };
    }

    /// <summary>
    /// “Mark all read” from either menu: marks, then offers the exact
    /// undo in the ticker footer (v0.3.0) — only the ids this call newly
    /// marked can be restored.
    /// </summary>
    private void MarkAllReadWithUndo()
    {
        if (_poller is null)
            return;
        var undo = _poller.MarkAllRead();
        if (undo.NewlyMarkedIds.Count > 0)
            _ticker?.ShowUndoBar(undo);
    }

    /// <summary>
    /// Toast + sound for genuinely new notifications. v0.3.0: the Core
    /// toast decision (pop-ups on, “alert me about” type rules, quiet
    /// hours) decides first; the shell's Focus Assist / presentation
    /// state composes on top. The sound follows the same decision.
    /// </summary>
    private void OnNewItems(IReadOnlyList<NotificationItem> newItems)
    {
        if (newItems.Count == 0)
            return;
        var now = DateTimeOffset.Now;
        if (!ToastDecision.ShouldToast(Settings, newItems[0], now))
        {
            if (Settings.ShowToasts && QuietHours.IsQuiet(now, Settings))
                Log.Info($"Suppressed pop-up for {newItems.Count} new notification(s): quiet hours");
            return;
        }
        if (!FocusAssistAllows())
            return;
        _toast ??= CreateToast();
        _toast.ShowNotification(newItems[0], newItems.Count - 1);
        PlaySoundIfEnabled();
    }

    /// <summary>
    /// The shell notification gate, logged once per state change (state
    /// names only). Any failure to read the state allows pop-ups.
    /// </summary>
    private bool FocusAssistAllows()
    {
        var allows = FocusAssistGate.AllowsNotifications(out var stateName);
        if (stateName != _lastGateState)
        {
            _lastGateState = stateName;
            Log.Info(allows
                ? $"Windows notification state: {stateName} — pop-ups allowed"
                : $"Windows notification state: {stateName} — pop-ups suppressed (counting continues)");
        }
        return allows;
    }

    private void PlaySoundIfEnabled()
    {
        if (!Settings.PlaySoundOnToast)
            return;
        try { SystemSounds.Asterisk.Play(); }
        catch { /* sound is decoration; never fail on it */ }
    }

    private ToastWindow CreateToast()
    {
        var toast = new ToastWindow();
        toast.ToastClicked += item =>
        {
            _poller?.MarkRead(item.Id);
            BrowserOpener.Open(item.Href, Log);
        };
        return toast;
    }

    // ---------- updates ----------

    private async Task CheckForUpdateAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUrl);
            request.Headers.UserAgent.ParseAdd($"FaceTray/{AppVersion}");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var response = await Http.SendAsync(request, cts.Token);
            var json = await response.Content.ReadAsStringAsync(cts.Token);
            if (UpdateInfo.TryParse(json, out var info) && info is not null && info.IsNewerThan(AppVersion))
            {
                _availableUpdate = info;
                Log.Info($"Update available: {info.Tag} (running {AppVersion})");
                _tray?.SetUpdateAvailable(info.Tag);
            }
            else
            {
                Log.Info("Update check: up to date");
            }
        }
        catch (Exception ex)
        {
            // Silent by design (offline, GitHub down, rate-limited…): one
            // log line, never a user-facing nag.
            Log.Info($"Update check failed ({ex.GetType().Name}); continuing silently");
        }
    }

    private void OpenReleasePage(string url)
    {
        // Only ever open the known release area of our own repo.
        if (!url.StartsWith(ReleasePagePrefix, StringComparison.Ordinal))
        {
            Log.Warn("Refused to open an unexpected update URL");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Open release page failed: {ex.Message}");
        }
    }

    private string UpdateStatusText() =>
        _availableUpdate is not null
            ? $"Update available: {_availableUpdate.Tag} — open it from the tray menu's “Update available” entry."
            : Settings.CheckForUpdates
                ? $"You're up to date (checked when FaceTray started; running v{AppVersion})."
                : "Update checks are off. FaceTray never downloads anything by itself either way.";

    // ---------- profiles ----------

    private void RefreshTrayProfileLabel() =>
        _tray?.SetProfileLabel(Settings.Profiles.Count > 1 ? Settings.ActiveProfile.Name : null);

    private void SyncHotkey()
    {
        if (_hotkey is null)
            return;
        if (Settings.GlobalHotkeyEnabled)
            _hotkey.Register();
        else
            _hotkey.Unregister();
    }

    /// <summary>Deletes one profile's session folder and data files. False when anything had to be deferred.</summary>
    private bool TryDeleteProfileData(string profileId)
    {
        var ok = true;
        try
        {
            var dir = AppPaths.WebViewDataDirFor(profileId);
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch { ok = false; }

        foreach (var path in new[]
                 {
                     AppPaths.ReadStatePathFor(profileId),
                     AppPaths.CachePathFor(profileId),
                     AppPaths.SnoozePathFor(profileId),
                     AppPaths.PinPathFor(profileId),
                     AppPaths.AlertTrackPathFor(profileId),
                 })
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch { ok = false; }
        }
        if (ok)
            Log.Info($"Deleted data for removed profile {profileId}");
        return ok;
    }

    /// <summary>Deletes data for profiles present in <paramref name="before"/> but gone in <paramref name="after"/>.</summary>
    private void ReconcileProfileRemovals(AppSettings before, AppSettings after)
    {
        var kept = after.Profiles.Select(p => p.Id).ToHashSet();
        var deferred = new List<string>();
        foreach (var removed in before.Profiles.Where(p => !kept.Contains(p.Id)))
        {
            if (!TryDeleteProfileData(removed.Id))
                deferred.Add(removed.Id);
        }
        if (deferred.Count > 0)
        {
            after.PendingProfileDeletions = deferred;
            SettingsStore.Save(AppPaths.SettingsPath, after);
            Log.Warn($"Profile data deletion deferred for: {string.Join(", ", deferred)} (retried on next start)");
        }
    }

    private void ProcessPendingProfileDeletions()
    {
        if (Settings.PendingProfileDeletions.Count == 0)
            return;
        var remaining = Settings.PendingProfileDeletions
            .Where(id => !TryDeleteProfileData(id))
            .ToList();
        if (remaining.Count != Settings.PendingProfileDeletions.Count)
        {
            Settings.PendingProfileDeletions = remaining;
            SettingsStore.Save(AppPaths.SettingsPath, Settings);
        }
    }

    /// <summary>Saves settings, restarts the process into the newly active profile, and exits.</summary>
    private void SwitchProfile(AppSettings saved)
    {
        var before = Settings;
        Settings = saved;
        SettingsStore.Save(AppPaths.SettingsPath, saved);
        ReconcileProfileRemovals(before, saved);
        Log.Info($"Switching profile to {saved.ActiveProfileId}; restarting");
        RelaunchAndExit();
    }

    /// <summary>
    /// Starts a fresh copy of FaceTray and exits this one. The relaunch is
    /// delayed: the new process must wait for this one to release the
    /// single-instance mutex (MuseDesktop pattern). Used by profile
    /// switches and by backup restores (v0.3.0).
    /// </summary>
    private void RelaunchAndExit()
    {
        var exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe))
        {
            try
            {
                Process.Start(new ProcessStartInfo(
                    "cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & start \"\" \"{exe}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
            }
            catch (Exception ex)
            {
                Log.Error($"Relaunch failed: {ex.Message}");
            }
        }
        ExitApp();
    }

    // ---------- backup / restore (v0.3.0) ----------

    private static ProfileFilePaths ProfileFilePathsFor(string profileId) => new(
        AppPaths.ReadStatePathFor(profileId),
        AppPaths.SnoozePathFor(profileId),
        AppPaths.PinPathFor(profileId),
        AppPaths.CachePathFor(profileId));

    private string ProvideBackupJson()
    {
        var json = BackupService.Build(Settings, ProfileFilePathsFor, DateTimeOffset.UtcNow);
        Log.Info("Backup exported from Settings (settings + local state for every profile; never the Facebook session)");
        return json;
    }

    /// <summary>Returns null on success, or the user-facing error text.</summary>
    private string? TryRestoreBackup(string json)
    {
        if (!BackupService.TryParse(json, out var envelope) || envelope is null)
            return "That file is not a valid FaceTray backup — its format or version was not recognized. Nothing was changed.";
        try
        {
            var restoredSettings = (envelope.Settings ?? new AppSettings()).Sanitized();
            Settings = restoredSettings;
            SettingsStore.Save(AppPaths.SettingsPath, restoredSettings);
            var report = BackupService.Restore(envelope, ProfileFilePathsFor);
            Log.Info($"Backup restored: {report.ProfilesRestored} profile(s), {report.ReadMarks} read marks, " +
                     $"{report.Snoozes} snoozes, {report.Pins} pins, {report.CachedItems} cached notifications");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error($"Backup restore failed: {ex.GetType().Name}: {ex.Message}");
            return "The backup could not be restored completely. The log has the details.";
        }
    }

    // ---------- windows ----------

    private void ShowLogin()
    {
        if (_loginWindow is not null)
        {
            _loginWindow.Activate();
            return;
        }
        _loginWindow = new LoginWindow(Log, _sessionDir);
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
        _settingsWindow = new SettingsWindow(
            Settings, Log, _sessionDir, BuildDiagnosticsState, UpdateStatusText(),
            ProvideBackupJson, TryRestoreBackup);
        _settingsWindow.Saved += saved =>
        {
            var before = Settings;
            Settings = saved;
            ReconcileProfileRemovals(before, saved);
            _poller?.ApplySettings(saved);
            if (_ticker is not null)
                _ticker.DisplaySettings = saved;
            RefreshTrayProfileLabel();
            _tray?.SetAccentColor(saved.ActiveProfile.AccentColor);
            SyncHotkey();
        };
        _settingsWindow.SwitchProfileRequested += saved =>
        {
            _settingsWindow = null;
            SwitchProfile(saved);
        };
        _settingsWindow.RestoreCompleted += () =>
        {
            _settingsWindow = null;
            Log.Info("Backup restored; restarting to apply it");
            RelaunchAndExit();
        };
        _settingsWindow.ClearDataRequested += () =>
        {
            _readStore.Clear();
            _snoozeStore.Clear();
            _pinStore.Clear();
            _alertTrackStore.Clear();
            NotificationCacheStore.Clear(_cachePath);
            _poller?.ClearLocalState();
            _tray?.SetUnreadCount(0);
        };
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private DiagnosticsState BuildDiagnosticsState() => new()
    {
        AppVersion = AppVersion,
        ActiveProfileId = Settings.ActiveProfileId,
        ActiveProfileName = Settings.ActiveProfile.Name,
        ProfileCount = Settings.Profiles.Count,
        LastPollUtc = _poller?.LastPoll?.AtUtc,
        LastPollRows = _poller?.LastPoll?.Rows ?? 0,
        LastPollUnread = _poller?.LastPoll?.Unread ?? 0,
        ExtractionHealth = _poller?.LastPoll?.Health ?? ExtractionHealthState.Healthy,
        LastStrategy = _poller?.LastStrategy ?? 0,
        StrategyCounts = _poller?.StrategyCounts ?? new Dictionary<int, int>(),
        SessionStateName = _poller?.SessionState.ToString() ?? "",
        PollIntervalSeconds = Settings.PollIntervalSeconds,
        CachedItemCount = _poller?.Items.Count ?? 0,
        UnreadCount = _poller?.UnreadCount ?? 0,
        SessionFolder = _sessionDir,
    };

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
            _toast?.Close();
            _ticker?.Close();
            _hotkey?.Dispose();
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
        BuildProfileContext();
        PollerService poller;
        TickerWindow ticker;
        try
        {
            poller = new PollerService(Settings, Log, _readStore, _snoozeStore, _pinStore, _alertTrackStore, _cachePath, _sessionDir, Dispatcher);
            ticker = new TickerWindow(poller, Log) { DisplaySettings = Settings };
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
