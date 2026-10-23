using System.Windows;
using FaceTray.Core;
using Microsoft.Web.WebView2.Core;

namespace FaceTray;

/// <summary>
/// The visible sign-in window. It uses the SAME shared WebView2
/// environment/profile as the poller, so signing in here is what signs
/// the poller in. Closing it asks the poller to refresh.
/// </summary>
public partial class LoginWindow : Window
{
    private readonly DiagnosticLog _log;
    private bool _initialized;

    /// <summary>Raised after the window closes so the app can refresh the poller.</summary>
    public event Action? SignInFinished;

    public LoginWindow(DiagnosticLog log)
    {
        _log = log;
        InitializeComponent();
        Loaded += async (_, _) => await InitAsync();
        Closed += (_, _) => SignInFinished?.Invoke();
    }

    private async Task InitAsync()
    {
        if (_initialized)
        {
            Browser.CoreWebView2?.Navigate(UrlGuard.FacebookHomeUrl);
            return;
        }
        try
        {
            var env = await WebViewEnv.GetAsync();
            await Browser.EnsureCoreWebView2Async(env);
            Browser.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            _initialized = true;
            StatusText.Text = "Loading Facebook…";
            Browser.CoreWebView2.Navigate(UrlGuard.FacebookHomeUrl);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            StatusText.Text = "The WebView2 Runtime is missing. Install Microsoft's WebView2 Runtime, then reopen this window.";
            _log.Error("Login window: WebView2 Runtime not found");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Facebook couldn't be loaded here. Check your connection and try again.";
            _log.Error($"Login window init failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            StatusText.Text = "That page didn't load. Check your connection — you can keep trying here.";
            return;
        }
        var source = Browser.CoreWebView2.Source;
        StatusText.Text = UrlGuard.LooksLikeLoginUrl(source)
            ? "Enter your Facebook details below to sign in."
            : "You appear to be signed in. You can close this window now.";
    }
}
