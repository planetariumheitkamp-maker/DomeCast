using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DomeCast.Capture;

internal sealed record CapturableWindow(IntPtr Hwnd, string Title, string ProcessName);

/// <summary>Lists top-level visible windows that WGC can capture.</summary>
internal static class WindowEnumerator
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();

    internal static List<CapturableWindow> GetWindows()
    {
        var shell = GetShellWindow();
        var self = Environment.ProcessId;
        var list = new List<CapturableWindow>();

        EnumWindows((hWnd, _) =>
        {
            if (hWnd == shell || !IsWindowVisible(hWnd)) return true;
            var sb = new StringBuilder(512);
            if (GetWindowTextW(hWnd, sb, sb.Capacity) == 0) return true;
            string title = sb.ToString().Trim();
            if (title.Length == 0) return true;

            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == self) return true; // don't offer our own console

            string proc;
            try { proc = Process.GetProcessById((int)pid).ProcessName; }
            catch { proc = "?"; }

            list.Add(new CapturableWindow(hWnd, title, proc));
            return true;
        }, IntPtr.Zero);

        return list;
    }
}
