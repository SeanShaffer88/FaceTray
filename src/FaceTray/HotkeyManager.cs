using System.Runtime.InteropServices;
using System.Windows.Interop;
using FaceTray.Core;

namespace FaceTray;

/// <summary>
/// The Ctrl+Shift+F global hotkey (v0.2.0), via RegisterHotKey on a
/// message-only window. Registration is best-effort: if another app owns
/// the chord, FaceTray logs one line and carries on without it.
/// Unregistered on exit (and the OS releases it with the process anyway).
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int HotkeyId = 0xF7CE;
    private const int WmHotkey = 0x0312;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkF = 0x46;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly DiagnosticLog _log;
    private readonly HwndSource _source;
    private bool _registered;
    private bool _disposed;

    /// <summary>Raised (on the UI thread) when the chord is pressed.</summary>
    public event Action? Pressed;

    public HotkeyManager(DiagnosticLog log)
    {
        _log = log;
        var parameters = new HwndSourceParameters("FaceTrayHotkey")
        {
            Width = 0,
            Height = 0,
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE: never visible
            WindowStyle = 0,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public bool IsRegistered => _registered;

    public void Register()
    {
        if (_registered || _disposed)
            return;
        if (RegisterHotKey(_source.Handle, HotkeyId, ModControl | ModShift | ModNoRepeat, VkF))
        {
            _registered = true;
            _log.Info("Global hotkey Ctrl+Shift+F registered");
        }
        else
        {
            _log.Warn("Global hotkey Ctrl+Shift+F could not be registered (another app may use it); continuing without it");
        }
    }

    public void Unregister()
    {
        if (!_registered)
            return;
        try { UnregisterHotKey(_source.Handle, HotkeyId); }
        catch { /* process exit cleans up regardless */ }
        _registered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Unregister();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
