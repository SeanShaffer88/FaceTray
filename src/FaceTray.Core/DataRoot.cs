namespace FaceTray.Core;

/// <summary>
/// Portable mode (v0.3.0): when a file named <c>portable.txt</c> sits next
/// to FaceTray.exe, ALL of FaceTray's data — settings, cache, logs and the
/// WebView2 sign-in sessions — lives in a <c>FaceTray-data</c> folder
/// beside the exe instead of %APPDATA% / %LOCALAPPDATA%, so the whole
/// app can be carried on a USB stick. The marker is created by the user;
/// FaceTray never ships one and never creates one itself. Pure and
/// probe-injected so tests can pin the rule without touching the disk.
/// </summary>
public static class DataRoot
{
    public const string MarkerFileName = "portable.txt";
    public const string DataFolderName = "FaceTray-data";

    /// <summary>
    /// The portable data root for the app's base directory, or null when
    /// the marker file is absent (the normal AppData behaviour).
    /// </summary>
    public static string? TryResolvePortableRoot(string appBaseDir, Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(appBaseDir) || fileExists is null)
            return null;
        return fileExists(Path.Combine(appBaseDir, MarkerFileName))
            ? Path.Combine(appBaseDir, DataFolderName)
            : null;
    }
}
