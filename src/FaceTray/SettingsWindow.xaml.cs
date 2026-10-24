using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using FaceTray.Core;
using Microsoft.Win32;
using Clipboard = System.Windows.Clipboard;
using ComboBox = System.Windows.Controls.ComboBox;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace FaceTray;

/// <summary>
/// Edits a clone of the settings; Save persists atomically, syncs
/// autostart, and lets the app apply everything live where possible.
/// v0.2.0 sections: quiet hours / sound / hotkey, update check, type
/// filters, ticker appearance + text size, history pruning, multi-account
/// profiles, diagnostics, and settings export/import.
/// Structural profile changes are reconciled by App on Save (removed
/// accounts have their data deleted there); “Switch” takes its own event
/// because it saves and restarts immediately.
/// </summary>
public partial class SettingsWindow : Window
{
    private AppSettings _editing;
    private readonly DiagnosticLog _log;
    private readonly Func<DiagnosticsState>? _diagnosticsProvider;

    /// <summary>Raised with the saved settings after a successful Save.</summary>
    public event Action<AppSettings>? Saved;

    /// <summary>Raised when the user confirms Clear data.</summary>
    public event Action? ClearDataRequested;

    /// <summary>
    /// Raised when the user chooses “Switch to selected”: the settings
    /// (with the new ActiveProfileId) are final — App persists them and
    /// restarts into the other account. The window closes itself.
    /// </summary>
    public event Action<AppSettings>? SwitchProfileRequested;

    public SettingsWindow(
        AppSettings current,
        DiagnosticLog log,
        string sessionFolder,
        Func<DiagnosticsState>? diagnosticsProvider = null,
        string? updateStatusText = null)
    {
        _editing = current.Clone();
        _log = log;
        _diagnosticsProvider = diagnosticsProvider;
        InitializeComponent();

        ProfilePathText.Text = $"Facebook session folder (active account): {sessionFolder}";
        UpdateStatusText.Text = updateStatusText ?? "";
        ProfilesList.SelectionChanged += (_, _) => UpdateProfileButtons();
        RefreshControls();
        RefreshDiagnostics();
    }

    // ---------- control <-> settings mapping ----------

    private void RefreshControls()
    {
        SelectComboByTag(PollIntervalBox, _editing.PollIntervalSeconds.ToString());
        StartWithWindowsBox.IsChecked = _editing.StartWithWindows;
        ShowToastsBox.IsChecked = _editing.ShowToasts;
        PlaySoundBox.IsChecked = _editing.PlaySoundOnToast;
        QuietHoursBox.IsChecked = _editing.QuietHoursEnabled;
        QuietStartBox.Text = _editing.QuietHoursStart;
        QuietEndBox.Text = _editing.QuietHoursEnd;
        GlobalHotkeyBox.IsChecked = _editing.GlobalHotkeyEnabled;
        CheckForUpdatesBox.IsChecked = _editing.CheckForUpdates;

        var hidden = _editing.HiddenTypeGroups.ToHashSet();
        ShowCommentsMentionsBox.IsChecked = !hidden.Contains(NotificationTypeGroups.Key(NotificationTypeGroup.CommentsAndMentions));
        ShowLikesBox.IsChecked = !hidden.Contains(NotificationTypeGroups.Key(NotificationTypeGroup.LikesAndReactions));
        ShowBirthdaysBox.IsChecked = !hidden.Contains(NotificationTypeGroups.Key(NotificationTypeGroup.Birthdays));
        ShowOtherBox.IsChecked = !hidden.Contains(NotificationTypeGroups.Key(NotificationTypeGroup.Other));

        SelectComboByTag(ThemeBox, _editing.TickerTheme);
        SelectComboByTag(TextScaleBox, _editing.TickerTextScalePercent.ToString());
        SelectComboByTag(HistoryBox, _editing.HistoryKeepDays.ToString());
        RefreshProfilesList();
    }

    private static void SelectComboByTag(ComboBox box, string tag)
    {
        foreach (ComboBoxItem item in box.Items)
        {
            if (item.Tag?.ToString() == tag)
            {
                box.SelectedItem = item;
                return;
            }
        }
        if (box.Items.Count > 0)
            box.SelectedIndex = 0;
    }

    /// <summary>Reads every control into the editing clone. False (with a message shown) on invalid input.</summary>
    private bool ReadControls()
    {
        if (PollIntervalBox.SelectedItem is ComboBoxItem item
            && int.TryParse(item.Tag?.ToString(), out var seconds))
            _editing.PollIntervalSeconds = seconds;
        _editing.StartWithWindows = StartWithWindowsBox.IsChecked == true;
        _editing.ShowToasts = ShowToastsBox.IsChecked == true;
        _editing.PlaySoundOnToast = PlaySoundBox.IsChecked == true;
        _editing.QuietHoursEnabled = QuietHoursBox.IsChecked == true;

        if (!TimeOfDayText.TryParse(QuietStartBox.Text, out var quietStart)
            || !TimeOfDayText.TryParse(QuietEndBox.Text, out var quietEnd))
        {
            MessageBox.Show("Quiet hours need 24-hour times like 22:00 and 07:00.",
                "FaceTray", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        _editing.QuietHoursStart = TimeOfDayText.Format(quietStart);
        _editing.QuietHoursEnd = TimeOfDayText.Format(quietEnd);

        _editing.GlobalHotkeyEnabled = GlobalHotkeyBox.IsChecked == true;
        _editing.CheckForUpdates = CheckForUpdatesBox.IsChecked == true;

        var hidden = new List<string>();
        if (ShowCommentsMentionsBox.IsChecked != true)
            hidden.Add(NotificationTypeGroups.Key(NotificationTypeGroup.CommentsAndMentions));
        if (ShowLikesBox.IsChecked != true)
            hidden.Add(NotificationTypeGroups.Key(NotificationTypeGroup.LikesAndReactions));
        if (ShowBirthdaysBox.IsChecked != true)
            hidden.Add(NotificationTypeGroups.Key(NotificationTypeGroup.Birthdays));
        if (ShowOtherBox.IsChecked != true)
            hidden.Add(NotificationTypeGroups.Key(NotificationTypeGroup.Other));
        _editing.HiddenTypeGroups = hidden;

        if (ThemeBox.SelectedItem is ComboBoxItem theme)
            _editing.TickerTheme = theme.Tag?.ToString() ?? TickerThemes.FacebookDark;
        if (TextScaleBox.SelectedItem is ComboBoxItem scale
            && int.TryParse(scale.Tag?.ToString(), out var scaleValue))
            _editing.TickerTextScalePercent = scaleValue;
        if (HistoryBox.SelectedItem is ComboBoxItem history
            && int.TryParse(history.Tag?.ToString(), out var days))
            _editing.HistoryKeepDays = days;

        _editing.Sanitized();
        return true;
    }

    // ---------- save / cancel ----------

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!ReadControls())
            return;
        SettingsStore.Save(AppPaths.SettingsPath, _editing);
        Autostart.Sync(_editing.StartWithWindows);
        _log.Info("Settings saved");
        Saved?.Invoke(_editing);
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    // ---------- profiles ----------

    private void RefreshProfilesList(string? selectId = null)
    {
        ProfilesList.Items.Clear();
        foreach (var profile in _editing.Profiles)
        {
            var label = profile.Id == _editing.ActiveProfileId
                ? $"{profile.Name} (active)"
                : profile.Name;
            var entry = new ListBoxItem { Content = label, Tag = profile.Id };
            ProfilesList.Items.Add(entry);
            if (profile.Id == (selectId ?? _editing.ActiveProfileId))
                ProfilesList.SelectedItem = entry;
        }
        UpdateProfileButtons();
    }

    private ProfileInfo? SelectedProfile =>
        (ProfilesList.SelectedItem as ListBoxItem)?.Tag is string id
            ? _editing.Profiles.FirstOrDefault(p => p.Id == id)
            : null;

    private void UpdateProfileButtons()
    {
        var selected = SelectedProfile;
        var isActive = selected is not null && selected.Id == _editing.ActiveProfileId;
        RenameProfileButton.IsEnabled = selected is not null;
        RemoveProfileButton.IsEnabled = selected is not null && !isActive;
        SwitchProfileButton.IsEnabled = selected is not null && !isActive;
    }

    private void OnAddProfile(object sender, RoutedEventArgs e)
    {
        if (_editing.Profiles.Count >= Profiles.MaxProfiles)
        {
            MessageBox.Show($"FaceTray supports up to {Profiles.MaxProfiles} accounts.",
                "FaceTray", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var name = PromptDialog.Show(this, "Add account", "Name for the new account:", "");
        if (name is null)
            return;
        var profile = new ProfileInfo { Id = Profiles.NewId(), Name = name };
        _editing.Profiles.Add(profile);
        RefreshProfilesList(profile.Id);
        _log.Info("Account profile added in Settings (applies on Save)");
    }

    private void OnRenameProfile(object sender, RoutedEventArgs e)
    {
        var selected = SelectedProfile;
        if (selected is null)
            return;
        var name = PromptDialog.Show(this, "Rename account", "Account name:", selected.Name);
        if (name is null)
            return;
        selected.Name = name;
        RefreshProfilesList(selected.Id);
    }

    private void OnRemoveProfile(object sender, RoutedEventArgs e)
    {
        var selected = SelectedProfile;
        if (selected is null || selected.Id == _editing.ActiveProfileId)
            return;
        var answer = MessageBox.Show(
            $"Remove the account “{selected.Name}”?\n\nWhen you Save, FaceTray deletes that account's sign-in " +
            "session and its local history from this PC. The Facebook account itself is not affected.",
            "FaceTray", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK)
            return;
        _editing.Profiles.Remove(selected);
        RefreshProfilesList();
        _log.Info("Account profile removed in Settings (data deleted on Save)");
    }

    private void OnSwitchProfile(object sender, RoutedEventArgs e)
    {
        var selected = SelectedProfile;
        if (selected is null || selected.Id == _editing.ActiveProfileId)
            return;
        if (!ReadControls())
            return;
        _editing.ActiveProfileId = selected.Id;
        _editing.Sanitized();
        SwitchProfileRequested?.Invoke(_editing);
        Close();
    }

    // ---------- diagnostics ----------

    private void RefreshDiagnostics()
    {
        if (_diagnosticsProvider is null)
        {
            DiagnosticsText.Text = "Diagnostics are unavailable.";
            return;
        }
        try
        {
            DiagnosticsText.Text = DiagnosticsReport.Build(_diagnosticsProvider());
        }
        catch (Exception ex)
        {
            DiagnosticsText.Text = $"Diagnostics failed: {ex.Message}";
        }
    }

    private void OnRefreshDiagnostics(object sender, RoutedEventArgs e) => RefreshDiagnostics();

    private void OnCopyDiagnostics(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(DiagnosticsText.Text);
            _log.Info("Diagnostics copied to clipboard");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't copy diagnostics: {ex.Message}",
                "FaceTray", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnOpenLogFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDir);
            Process.Start(new ProcessStartInfo(AppPaths.LogDir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't open the log folder: {ex.Message}",
                "FaceTray", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------- export / import / clear ----------

    private void OnExportSettings(object sender, RoutedEventArgs e)
    {
        if (!ReadControls())
            return;
        var dialog = new SaveFileDialog
        {
            Title = "Export FaceTray settings",
            Filter = "JSON settings (*.json)|*.json",
            FileName = "facetray-settings.json",
        };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            File.WriteAllText(dialog.FileName, SettingsStore.Export(_editing));
            _log.Info("Settings exported");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't export settings: {ex.Message}",
                "FaceTray", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnImportSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import FaceTray settings",
            Filter = "JSON settings (*.json)|*.json",
        };
        if (dialog.ShowDialog(this) != true)
            return;
        string json;
        try
        {
            json = File.ReadAllText(dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't read that file: {ex.Message}",
                "FaceTray", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!SettingsStore.TryImport(json, out var imported))
        {
            MessageBox.Show("That file isn't a FaceTray settings export — nothing was changed.",
                "FaceTray", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _editing = imported;
        RefreshControls();
        _log.Info("Settings imported in Settings window (applies on Save)");
        MessageBox.Show("Settings imported. Review them, then Save to apply — account changes apply on Save too.",
            "FaceTray", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnClearData(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            "Clear the active account's local data?\n\nThis removes its cached notifications, local read marks " +
            "and snoozes. It does not sign the account out of Facebook (see the note above for that).",
            "FaceTray", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK)
            return;
        ClearDataRequested?.Invoke();
        _log.Info("Local data cleared from Settings");
        MessageBox.Show("Local data cleared.", "FaceTray", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
