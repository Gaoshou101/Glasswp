using System.Runtime.InteropServices;
using System.Text;

namespace WallpaperBridge;

/// <summary>Check the actual native window under the cursor for each wallpaper activation.</summary>
public sealed class DesktopInputGuard(DesktopWindowEvents windowEvents)
{
    private object? lastCheck;
    public bool Available => OperatingSystem.IsWindows();
    public object? LastCheck => Volatile.Read(ref lastCheck);

    public bool Allows(DesktopInputPoint point)
    {
        if (!Available) return false;
        var previous = SetThreadDpiAwarenessContext(new nint(-4));
        try
        {
            if (!GetCursorPos(out var cursor)) return false;
            return AllowsAt(point, cursor.X, cursor.Y);
        }
        finally { if (previous != 0) SetThreadDpiAwarenessContext(previous); }
    }

    // Native integration checks can supply a point without moving the user's mouse.
    public bool AllowsAt(DesktopInputPoint point, int x, int y)
    {
        var previous = SetThreadDpiAwarenessContext(new nint(-4));
        try
        {
            var cursor = new Point { X = x, Y = y };
            var hit = WindowFromPoint(cursor);
            var root = GetAncestor(hit, 2); // GA_ROOT; desktop children share the Explorer root.
            var name = new StringBuilder(256);
            GetClassName(root, name, name.Capacity);
            GetWindowThreadProcessId(root, out var owner);
            GetWindowThreadProcessId(GetShellWindow(), out var shell);
            var desktop = root != 0 && shell != 0 && owner == shell && name.ToString() is "Progman" or "WorkerW";
            var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            var found = GetMonitorInfo(MonitorFromPoint(cursor, 0), ref monitor);
            var rect = monitor.Monitor;
            var windowTransition = windowEvents.Blocks(x, y);
            var allowed = found && !windowTransition && Matches(point, desktop, x, y, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
            Volatile.Write(ref lastCheck, new { at = DateTimeOffset.UtcNow, allowed, point, desktop, windowTransition,
                cursor = new { x, y }, monitor = new { rect.Left, rect.Top, width = rect.Right - rect.Left, height = rect.Bottom - rect.Top } });
            return allowed;
        }
        finally { if (previous != 0) SetThreadDpiAwarenessContext(previous); }
    }

    // Normalize to the full monitor to support negative origins and mixed DPI.
    public static bool Matches(DesktopInputPoint point, bool desktop,
        int x, int y, int left, int top, int width, int height)
    {
        if (!desktop || width <= 0 || height <= 0 ||
            !double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
            !double.IsFinite(point.Width) || !double.IsFinite(point.Height) ||
            point.Width is < 1 or > 16384 || point.Height is < 1 or > 16384 ||
            point.X < 0 || point.Y < 0 || point.X >= point.Width || point.Y >= point.Height) return false;
        if (Math.Abs((double)width / height - point.Width / point.Height) > .02) return false;
        return Math.Abs((double)(x - left) / width * point.Width - point.X) <= 6 &&
            Math.Abs((double)(y - top) / height * point.Height - point.Y) <= 6;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int size);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
}

public sealed record DesktopInputPoint(double X, double Y, double Width, double Height);
