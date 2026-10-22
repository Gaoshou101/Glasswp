using System.Runtime.InteropServices;

namespace WallpaperBridge;

/// <summary>Remember a minimize gesture before its window disappears from WindowFromPoint.</summary>
public sealed class DesktopWindowEvents : IHostedService
{
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? thread;
    private uint threadId;
    private nint hook;
    private WinEventCallback? callback;
    private Minimize? last;
    public bool Available => Volatile.Read(ref hook) != 0;
    public object? LastMinimize => Volatile.Read(ref last);

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
        return minimized is not null && Matches(Environment.TickCount64 - minimized.At, x, y, minimized.X, minimized.Y);
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
            ready.TrySetResult();
            if (hook != 0) while (GetMessage(out var message, 0, 0, 0) > 0)
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
        }
    }

    private void OnEvent(nint hook, uint type, nint window, int objectId, int childId, uint eventThread, uint eventTime)
    {
        if (window == 0 || !GetCursorPos(out var point)) return;
        Volatile.Write(ref last, new Minimize(Environment.TickCount64, point.X, point.Y));
    }

    private sealed record Minimize(long At, int X, int Y);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public Point Point; public uint Private; }
    private delegate void WinEventCallback(nint hook, uint type, nint window, int objectId, int childId, uint eventThread, uint eventTime);
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
