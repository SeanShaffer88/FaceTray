using Microsoft.Web.WebView2.Core;

namespace FaceTray;

/// <summary>
/// The one shared WebView2 environment (and therefore the one persistent
/// profile folder) for the whole process. The poller and the login window
/// MUST use the same environment instance: sign-ins are stored per user
/// data folder, and an environment is bound to its folder per process, so
/// a second environment over the same folder would fail.
///
/// The folder is the ACTIVE account profile's session folder (v0.2.0):
/// switching profiles restarts the app, so a process only ever has one.
/// </summary>
public static class WebViewEnv
{
    private static Task<CoreWebView2Environment>? _task;
    private static readonly object Gate = new();

    public static Task<CoreWebView2Environment> GetAsync(string userDataFolder)
    {
        lock (Gate)
        {
            _task ??= CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder,
                new CoreWebView2EnvironmentOptions());
            return _task;
        }
    }
}
