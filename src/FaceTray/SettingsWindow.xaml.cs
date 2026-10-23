using System.Windows;
using System.Windows.Controls;
using FaceTray.Core;
using MessageBox = System.Windows.MessageBox;

namespace FaceTray;

/// <summary>
/// Edits a clone of the settings; Save persists atomically, syncs
/// autostart, and lets the app apply the new poll interval immediately.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _editing;
    private readonly DiagnosticLog _log;

    /// <summary>Raised with the saved settings after a successful Save.</summary>
    public event Action<AppSettings>? Saved;

    /// <summary>Raised when the user confirms Clear data.</summary>
    public event Action? ClearDataRequested;

    public SettingsWindow(AppSettings current, DiagnosticLog log)
    {
        _editing = current.Clone();
        _log = log;
        InitializeComponent();

        foreach (ComboBoxItem item in PollIntervalBox.Items)
        {
            if (int.TryParse(item.Tag?.ToString(), out var seconds) && seconds == _editing.PollIntervalSeconds)
                PollIntervalBox.SelectedItem = item;
        }
        PollIntervalBox.SelectedItem ??= PollIntervalBox.Items[1];
        StartWithWindowsBox.IsChecked = _editing.StartWithWindows;
        ShowToastsBox.IsChecked = _editing.ShowToasts;
        ProfilePathText.Text = $"Facebook session folder: {AppPaths.WebViewDataDir}";
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (PollIntervalBox.SelectedItem is ComboBoxItem item
            && int.TryParse(item.Tag?.ToString(), out var seconds))
            _editing.PollIntervalSeconds = seconds;
        _editing.StartWithWindows = StartWithWindowsBox.IsChecked == true;
        _editing.ShowToasts = ShowToastsBox.IsChecked == true;
        _editing.Sanitized();

        SettingsStore.Save(AppPaths.SettingsPath, _editing);
        Autostart.Sync(_editing.StartWithWindows);
        _log.Info("Settings saved");
        Saved?.Invoke(_editing);
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnClearData(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            "Clear FaceTray's local data?\n\nThis removes the cached notifications and your local read marks. " +
            "It does not sign you out of Facebook (see the note above for that).",
            "FaceTray", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK)
            return;
        ClearDataRequested?.Invoke();
        _log.Info("Local data cleared from Settings");
        MessageBox.Show("Local data cleared.", "FaceTray", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
