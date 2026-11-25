using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace RocoPlayer.Playback;

public sealed class TargetWindow
{
    private readonly nint hwnd;
    private readonly uint pid;
    public string Title { get; }
    internal nint Handle => hwnd;

    private TargetWindow(nint hwnd, uint pid)
    {
        this.hwnd = hwnd;
        this.pid = pid;
        var title = new StringBuilder(512);
        GetWindowTextW(hwnd, title, title.Capacity);
        Title = title.Length == 0 ? $"窗口 {hwnd}" : title.ToString();
    }

    public static TargetWindow Capture() => FromHandle(GetForegroundWindow());

    internal static TargetWindow FromHandle(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (hwnd == 0 || pid == 0 || pid == Environment.ProcessId || !IsWindow(hwnd))
            throw new InvalidOperationException("请切到游戏窗口后再捕获");
        return new(hwnd, pid);
    }

    public string State
    {
        get
        {
            GetWindowThreadProcessId(hwnd, out uint currentPid);
            if (currentPid != pid || !IsWindow(hwnd)) return "closed";
            return GetForegroundWindow() == hwnd ? "ready" : "background";
        }
    }

    public bool Activate()
    {
        if (State == "closed") return false;
        if (IsIconic(hwnd)) ShowWindowAsync(hwnd, 9);
        SetForegroundWindow(hwnd);
        return State == "ready";
    }

    internal void EnsureForeground(CancellationToken token)
    {
        if (State == "ready" || Activate()) return;
        var clock = Stopwatch.StartNew();
        while (State == "background" && clock.ElapsedMilliseconds < 750)
        {
            token.ThrowIfCancellationRequested();
            token.WaitHandle.WaitOne(10);
        }
        if (State != "ready") throw new InvalidOperationException("无法切换到游戏窗口。请切回游戏，按 F8 开始");
    }

    public Task ActivateAsync(CancellationToken token) => Task.Run(() => EnsureForeground(token), token);

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint hwnd, StringBuilder text, int capacity);
}
