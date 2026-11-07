using System.Runtime.InteropServices;

namespace WallpaperBridge;

/// <summary>Remember disappearing app windows before a forwarded release reaches the wallpaper.</summary>
public sealed class DesktopWindowEvents : IHostedService
{
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? thread;
    private uint threadId;
    private nint hook;
    private nint lifetimeHook;
    private nint locationHook;
    private WinEventCallback? callback;
    private Minimize? last;
    private Disappearance? lastDisappearance;
    // Only accessed on the event thread. A destroy callback can arrive after HWND becomes invalid.
    private readonly Dictionary<nint, Rect> visibleWindows = [];
    public bool Available => Volatile.Read(ref hook) != 0;
    public bool CloseGuardAvailable => Volatile.Read(ref lifetimeHook) != 0 && Volatile.Read(ref locationHook) != 0;
    public object? LastMinimize => Volatile.Read(ref last);
    public object? LastWindowDisappearance => Volatile.Read(ref lastDisappearance);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        thread = new Thread(Run) { IsBackground = true, Name = "Wallpaper window events" };
        thread.Start();
        return ready.Task.WaitAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (threadId != 0) PostThreadMessage(threadId, 0x0012, 0, 0);
        if (thread is not null) await Task.Run(() => thread.Join(2000), cancellationToken);
    }

    public bool Blocks(int x, int y)
    {
        var minimized = Volatile.Read(ref last);
        var disappeared = Volatile.Read(ref lastDisappearance);
        return (minimized is not null && Matches(Environment.TickCount64 - minimized.At, x, y, minimized.X, minimized.Y)) ||
            (disappeared is not null && Matches(Environment.TickCount64 - disappeared.At, x, y, disappeared.X, disappeared.Y));
    }

    public static bool Matches(long ageMs, int x, int y, int minimizeX, int minimizeY) =>
        ageMs is >= 0 and <= 800 && Math.Abs((long)x - minimizeX) <= 12 && Math.Abs((long)y - minimizeY) <= 12;

    private void Run()
    {
        try
        {
            threadId = GetCurrentThreadId();
            SetThreadDpiAwarenessContext(new nint(-4));
            PeekMessage(out _, 0, 0, 0, 0);
            callback = OnEvent;
            // EVENT_SYSTEM_MINIMIZESTART, out-of-context; no mouse input hook or polling.
            hook = SetWinEventHook(0x0016, 0x0016, 0, callback, 0, 0, 0);
            // DESTROY / SHOW / HIDE, plus LOCATIONCHANGE to retain bounds before HWND destruction.
            lifetimeHook = SetWinEventHook(0x8001, 0x8003, 0, callback, 0, 0, 0);
            locationHook = SetWinEventHook(0x800B, 0x800B, 0, callback, 0, 0, 0);
            EnumWindows((window, _) => { TrackWindow(window); return true; }, 0);
            ready.TrySetResult();
            if (Available || CloseGuardAvailable) while (GetMessage(out var message, 0, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        finally
        {
            ready.TrySetResult();
            var installed = Interlocked.Exchange(ref hook, 0);
            if (installed != 0) UnhookWinEvent(installed);
            installed = Interlocked.Exchange(ref lifetimeHook, 0);
            if (installed != 0) UnhookWinEvent(installed);
            installed = Interlocked.Exchange(ref locationHook, 0);
            if (installed != 0) UnhookWinEvent(installed);
            visibleWindows.Clear();
        }
    }

    private void OnEvent(nint hook, uint type, nint window, int objectId, int childId, uint eventThread, uint eventTime)
    {
        if (window == 0) return;
        if (type == 0x0016)
        {
            if (GetCursorPos(out var point)) Volatile.Write(ref last, new Minimize(Environment.TickCount64, point.X, point.Y));
            return;
        }
        if (objectId != 0 || childId != 0) return; // OBJID_WINDOW / CHILDID_SELF only.
        if (type is 0x8002 or 0x800B) { TrackWindow(window); return; }
        if (type is not (0x8001 or 0x8003) || !visibleWindows.Remove(window, out var bounds)) return;
        // HIDE still has a valid HWND on many apps; DESTROY must use the remembered bounds.
        if (GetWindowRect(window, out var current)) bounds = current;
        if (!GetCursorPos(out var cursor) || cursor.X < bounds.Left || cursor.X >= bounds.Right ||
            cursor.Y < bounds.Top || cursor.Y >= bounds.Bottom) return;
        Volatile.Write(ref lastDisappearance, new Disappearance(Environment.TickCount64, cursor.X, cursor.Y,
            type == 0x8003 ? "hidden" : "destroyed"));
    }

    private void TrackWindow(nint window)
    {
        if (!IsWindowVisible(window)) return;
        var style = (long)GetWindowLongPtr(window, -16);
        var extended = (long)GetWindowLongPtr(window, -20);
        // Ignore child controls, menus, tooltips and tool windows. Borderless app windows may use APPWINDOW.
        if ((style & 0x40000000) != 0 || (extended & 0x80) != 0 ||
            ((style & 0x00C00000) != 0x00C00000 && (extended & 0x40000) == 0)) return;
        if (GetWindowRect(window, out var bounds)) visibleWindows[window] = bounds;
    }

    private sealed record Minimize(long At, int X, int Y);
    private sealed record Disappearance(long At, int X, int Y, string Kind);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public Point Point; public uint Private; }
    private delegate void WinEventCallback(nint hook, uint type, nint window, int objectId, int childId, uint eventThread, uint eventTime);
    private delegate bool EnumWindowsCallback(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventCallback callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Message message, nint window, uint min, uint max, uint flags);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, nint window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint message, nuint wParam, nint lParam);
}
