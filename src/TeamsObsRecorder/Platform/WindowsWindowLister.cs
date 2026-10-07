using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace TeamsObsRecorder.Platform;

/// <summary>Lists visible top-level windows with the Win32 API.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsWindowLister : IWindowLister
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    public IReadOnlyList<WindowInfo> List()
    {
        var results = new List<WindowInfo>();
        var names = new Dictionary<uint, string>();
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            var length = GetWindowTextLengthW(hWnd);
            if (length == 0) return true;
            var sb = new StringBuilder(length + 1);
            GetWindowTextW(hWnd, sb, sb.Capacity);
            GetWindowThreadProcessId(hWnd, out var pid);
            if (!names.TryGetValue(pid, out var name))
            {
                try { name = Process.GetProcessById((int)pid).ProcessName; }
                catch { name = ""; }
                names[pid] = name;
            }
            results.Add(new WindowInfo(hWnd.ToString("x"), sb.ToString(), name));
            return true;
        }, IntPtr.Zero);
        return results;
    }
}
