using System.Runtime.InteropServices;
using System.Text.Json;
using WallpaperBridge;

static class DesktopWindowEventChecks
{
    public static Task Run(Action<bool, string> check) => Task.Run(() => RunNative(check));

    private static void RunNative(Action<bool, string> check)
    {
        var events = new DesktopWindowEvents();
        // Invisible, click-through windows exercise real OS events without moving the mouse or taking focus.
        var startup = Create();
        try
        {
            ShowWindow(startup, 4);
            events.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            check(events.Available && events.CloseGuardAvailable, "minimize and close event subscriptions are available");
            HideAndCheck(startup, "hidden", "window already visible at bridge startup");
            var hidden = Create();
            try
            {
                ShowWindow(hidden, 4);
                Thread.Sleep(60);
                // Change bounds after SHOW, then verify that the disappearing window is still recognized.
                GetCursorPos(out var cursor);
                SetWindowPos(hidden, 1, cursor.X - 80, cursor.Y - 80, 160, 160, 0x0014);
                Thread.Sleep(60);
                HideAndCheck(hidden, "hidden", "close-to-tray style hide after move");
                ShowWindow(hidden, 4);
                Thread.Sleep(60);
                var previous = events.LastWindowDisappearance;
                DestroyWindow(hidden);
                WaitFor(() => !ReferenceEquals(events.LastWindowDisappearance, previous));
                // DestroyWindow normally emits HIDE before DESTROY; either must protect the release.
                CheckTransition(null, "destroyed top-level window");
                Thread.Sleep(820);
                GetCursorPos(out cursor);
                check(!events.Blocks(cursor.X, cursor.Y), "close protection expires for a later desktop click");
            }
            finally { DestroyWindow(hidden); }

            foreach (var (style, extended, label) in new[] {
                (0x00C00000u, 0x80u, "tool window"), (0x80000000u, 0u, "borderless popup without app-window style") })
            {
                var ignored = Create(style: style, extended: extended);
                try
                {
                    ShowWindow(ignored, 4);
                    Thread.Sleep(60);
                    var previous = events.LastWindowDisappearance;
                    ShowWindow(ignored, 0);
                    Thread.Sleep(60);
                    check(ReferenceEquals(previous, events.LastWindowDisappearance), label + " hide cannot suppress a desktop click");
                }
                finally { DestroyWindow(ignored); }
            }

            var outside = Create(x: -32000, y: -32000);
            try
            {
                ShowWindow(outside, 4);
                Thread.Sleep(60);
                var previous = events.LastWindowDisappearance;
                ShowWindow(outside, 0);
                Thread.Sleep(60);
                check(ReferenceEquals(previous, events.LastWindowDisappearance), "background window outside cursor does not block wallpaper");
                ShowWindow(outside, 7);
                WaitFor(() => events.LastMinimize is not null);
                check(events.LastMinimize is not null, "real native minimize notification is still observed");
                if (events.LastMinimize is not null)
                {
                    var point = JsonSerializer.SerializeToElement(events.LastMinimize);
                    check(events.Blocks(point.GetProperty("X").GetInt32(), point.GetProperty("Y").GetInt32()), "minimize release remains blocked after adding close protection");
                }
            }
            finally { DestroyWindow(outside); }

            ShowWindow(startup, 4);
            Thread.Sleep(60);
            var child = CreateWindowEx(0, "STATIC", "", 0x50C00000, 0, 0, 200, 200, startup, 0, 0, 0);
            check(child != 0, "native child-control fixture created");
            try
            {
                Thread.Sleep(60);
                var previous = events.LastWindowDisappearance;
                ShowWindow(child, 0);
                Thread.Sleep(60);
                check(ReferenceEquals(previous, events.LastWindowDisappearance), "child-control hide cannot suppress a desktop click");
            }
            finally { DestroyWindow(child); }
        }
        finally
        {
            DestroyWindow(startup);
            events.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        check(!events.Available && !events.CloseGuardAvailable, "all native event subscriptions detach on shutdown");

        void HideAndCheck(nint window, string kind, string label)
        {
            var previous = events.LastWindowDisappearance;
            ShowWindow(window, 0);
            WaitFor(() => !ReferenceEquals(events.LastWindowDisappearance, previous));
            CheckTransition(kind, label);
        }

        void CheckTransition(string? kind, string label)
        {
            var observed = events.LastWindowDisappearance;
            check(observed is not null, label + " disappearance observed");
            var point = JsonSerializer.SerializeToElement(observed);
            var observedKind = point.GetProperty("Kind").GetString();
            check(kind is null ? observedKind is "hidden" or "destroyed" : observedKind == kind, label + " has correct native event kind");
            var x = point.GetProperty("X").GetInt32();
            var y = point.GetProperty("Y").GetInt32();
            check(events.Blocks(x, y), label + " release is blocked at original position");
            check(!events.Blocks(x + 100, y + 100), label + " permits wallpaper cards elsewhere");
        }
    }

    private static void WaitFor(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++) Thread.Sleep(10);
    }

    private static nint Create(uint style = 0x00C00000, uint extended = 0, int? x = null, int? y = null)
    {
        GetCursorPos(out var cursor);
        var window = CreateWindowEx(0x08080020 | extended, "STATIC", "", style,
            x ?? cursor.X - 100, y ?? cursor.Y - 100, 200, 200, 0, 0, 0, 0);
        if (window == 0 || !SetLayeredWindowAttributes(window, 0, 0, 2)) throw new Exception("Native test window unavailable");
        return window;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint extended, string windowClass, string name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(nint window, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
}
