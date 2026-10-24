using System.Windows;

namespace FaceTray;

/// <summary>A one-line text prompt (account names in Settings).</summary>
public partial class PromptDialog : Window
{
    private PromptDialog(string title, string label, string initial)
    {
        InitializeComponent();
        Title = title;
        LabelText.Text = label;
        InputBox.Text = initial;
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    /// <summary>Shows the prompt modally; returns the trimmed text, or null when cancelled/empty.</summary>
    public static string? Show(Window owner, string title, string label, string initial)
    {
        var dialog = new PromptDialog(title, label, initial) { Owner = owner };
        if (dialog.ShowDialog() != true)
            return null;
        var text = dialog.InputBox.Text.Trim();
        return text.Length == 0 ? null : text;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
