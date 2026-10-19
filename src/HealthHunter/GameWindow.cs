using System.Diagnostics;
using System.Drawing;
using System.Text;

namespace HealthHunter;

/// <summary>
/// v6: where the game is on screen. Finds the game's main window, its client area in screen coordinates (what the
/// assistant captures and reads bars from), and which process owns the window in front (what the assistant attaches to).
/// Only reads window positions; nothing is sent to the game.
/// </summary>
public static class GameWindow
{
    /// <summary>Processes that are never a game, so the assistant doesn't try to attach to them when they're in front.</summary>
    private static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "searchhost", "searchui", "startmenuexperiencehost", "shellexperiencehost", "taskmgr", "lockapp",
        "applicationframehost", "systemsettings", "textinputhost", "sihost", "runtimebroker", "ctfmon", "cmd", "powershell",
        "windowsterminal", "conhost", "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "code", "devenv", "rider64",
        "notepad", "notepad++", "steam", "steamwebhelper", "epicgameslauncher", "galaxyclient", "upc", "ubisoftconnect",
        "origin", "eadesktop", "battle.net", "discord", "spotify", "obs64", "teams", "ms-teams", "outlook", "winword", "excel",
        "powerpnt", "onenote", "slack", "zoom", "vlc", "mpc-hc64", "cheatengine-x86_64", "wemod", "healthhunter"
    };

    public static bool LooksLikeGame(string processName) => !string.IsNullOrEmpty(processName) && !NotGames.Contains(processName);

    /// <summary>The process that owns the window in front, or null for the desktop / HealthHunter itself.</summary>
    public static (int Pid, IntPtr Hwnd) Foreground()
    {
        var h = Native.GetForegroundWindow();
        if (h == IntPtr.Zero) return (0, IntPtr.Zero);
        Native.GetWindowThreadProcessId(h, out uint pid);
        if (pid == 0 || pid == Environment.ProcessId) return (0, IntPtr.Zero);
        return ((int)pid, h);
    }

    /// <summary>The largest visible window of a process (the game's main window; launchers and overlays are smaller).</summary>
    public static IntPtr MainWindowOf(int pid)
    {
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        Native.EnumWindows((h, _) =>
        {
            Native.GetWindowThreadProcessId(h, out uint p);
            if (p != pid || !Native.IsWindowVisible(h) || Native.IsIconic(h)) return true;
            if (!Native.GetClientRect(h, out var r)) return true;
            long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
            if (area > bestArea) { bestArea = area; best = h; }
            return true;
        }, IntPtr.Zero);
        if (best == IntPtr.Zero)
        {
            try { using var p = Process.GetProcessById(pid); best = p.MainWindowHandle; } catch { }
        }
        return best;
    }

    /// <summary>Client area of a window in screen coordinates, or empty when it's hidden or minimized.</summary>
    public static Rectangle ClientOnScreen(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd)) return Rectangle.Empty;
        if (!Native.GetClientRect(hwnd, out var r)) return Rectangle.Empty;
        var pt = new Native.POINT { X = 0, Y = 0 };
        Native.ClientToScreen(hwnd, ref pt);
        var rect = new Rectangle(pt.X, pt.Y, r.Right - r.Left, r.Bottom - r.Top);
        // Clip to the desktop: a window half off-screen can't be captured there.
        var vs = SystemInformation.VirtualScreen;
        rect.Intersect(vs);
        return rect;
    }

    public static string Title(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        try { Native.GetWindowTextW(hwnd, sb, sb.Capacity); } catch { }
        return sb.ToString();
    }

    /// <summary>True when any of these virtual keys is held right now.</summary>
    public static bool AnyKeyDown(params int[] vkeys)
    {
        foreach (var k in vkeys)
            if ((Native.GetAsyncKeyState(k) & 0x8000) != 0) return true;
        return false;
    }
}
