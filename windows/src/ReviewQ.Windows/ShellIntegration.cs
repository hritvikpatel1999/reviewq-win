using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using ReviewQ.Core;

namespace ReviewQ.Windows;

public sealed class ShellIntegration : IDisposable
{
    private const int ShortcutId = 0x5251;
    private readonly IntPtr handle;
    private readonly HwndSource source;
    private readonly Action activate;
    private readonly Action restoreTray;
    private readonly uint taskbarCreated;
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);
    public ShellIntegration(System.Windows.Window window, Action activate, Action restoreTray)
    {
        this.activate = activate; this.restoreTray = restoreTray;
        handle = new WindowInteropHelper(window).EnsureHandle();
        source = HwndSource.FromHwnd(handle) ?? throw new ReviewException("The ReviewQ window handle is unavailable.");
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        source.AddHook(Hook);
    }
    public static (uint Modifiers, uint Key) Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (0, 0);
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        uint modifiers = 0x4000;
        foreach (var part in parts[..^1])
        {
            uint bit = part.ToLowerInvariant() switch { "ctrl" => 2, "alt" => 1, "shift" => 4, "win" => 8, _ => 0 };
            if (bit == 0 || (modifiers & bit) != 0) throw Invalid();
            modifiers |= bit;
        }
        if (parts.Length < 2 || !Enum.TryParse<Key>(parts[^1], true, out var key) ||
            !parts[^1].Equals(key.ToString(), StringComparison.OrdinalIgnoreCase) ||
            !((key >= Key.A && key <= Key.Z) || (key >= Key.F1 && key <= Key.F24) || (key >= Key.D0 && key <= Key.D9)))
            throw Invalid();
        return (modifiers, (uint)KeyInterop.VirtualKeyFromKey(key));
    }
    public void Register(string text)
    {
        var parsed = Parse(text);
        UnregisterHotKey(handle, ShortcutId);
        if (parsed.Key != 0 && !RegisterHotKey(handle, ShortcutId, parsed.Modifiers, parsed.Key))
            throw new ReviewException("The global shortcut is unavailable or used by another app. Choose another combination in Settings, or use Retry.");
    }
    private static ReviewException Invalid() => new("Use a shortcut such as Ctrl+Alt+Q or Ctrl+Shift+F8, or leave it blank to disable.");
    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam, ref bool handled)
    {
        if (message == 0x0312 && wparam.ToInt32() == ShortcutId) { activate(); handled = true; }
        if ((uint)message == taskbarCreated) restoreTray();
        return IntPtr.Zero;
    }
    public void Dispose() { UnregisterHotKey(handle, ShortcutId); source.RemoveHook(Hook); }
}
