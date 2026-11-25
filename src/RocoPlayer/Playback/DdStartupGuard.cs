using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RocoPlayer.Playback;

// Native startup and its queued HID events are handled before the first DD performance.
internal sealed class DdStartupGuard : IDisposable
{
    private const uint FinishMessage = 0x8001, TimerMessage = 0x0113;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread thread;
    private volatile bool abort;
    private volatile uint threadId;
    private DdStartupKeys? keys;
    private Exception? failure;

    public DdStartupGuard()
    {
        thread = new Thread(Listen) { IsBackground = true, Name = "DD startup guard" };
        thread.Start();
        try { ready.Task.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); }
        catch { Abort(); throw; }
    }

    private void Listen()
    {
        nint hook = 0;
        nuint timer = 0;
        long started = Environment.TickCount64, completed = -1;
        Callback callback = Filter;
        try
        {
            PeekMessageW(out _, 0, 0, 0, 0);
            threadId = GetCurrentThreadId();
            if (abort) return;
            keys = new(GetAsyncKeyState(0x5B) < 0, GetAsyncKeyState(0x5C) < 0);
            timer = SetTimer(0, 0, 50, 0);
            if (timer == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "DD 初始化保护计时器无法启动");
            hook = SetWindowsHookExW(13, callback, GetModuleHandleW(null), 0);
            if (hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "DD 初始化保护无法安装");
            ready.TrySetResult();
            while (!abort)
            {
                int status = GetMessageW(out var msg, 0, 0, 0);
                if (status < 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "DD 初始化保护消息循环中断");
                if (status == 0) break;
                long now = Environment.TickCount64;
                if (msg.Message == FinishMessage) completed = now;
                if (msg.Message == TimerMessage && msg.WParam == timer && keys.CanFinish(completed, now)) break;
            }
        }
        catch (Exception ex) { failure = ex; ready.TrySetException(ex); }
        finally
        {
            if (hook != 0) UnhookWindowsHookEx(hook);
            if (timer != 0) KillTimer(0, timer);
            GC.KeepAlive(callback);
            DdDiagnostics.Log("guard_stopped", new
            {
                suppressed = keys?.Suppressed ?? 0, pending = keys?.Pending ?? 0, duration_ms = Environment.TickCount64 - started,
                last_win_after_init_ms = completed >= 0 && keys is { LastWin: >= 0 } ? keys.LastWin - completed : (long?)null,
                error = failure?.Message
            });
        }
    }

    private nint Filter(int code, nuint message, nint data)
    {
        if (code == 0 && message is 0x100 or 0x101 or 0x104 or 0x105 &&
            keys!.Intercept(Marshal.ReadInt32(data), message is 0x100 or 0x104, Environment.TickCount64))
            return 1;
        return CallNextHookEx(0, code, message, data);
    }

    private void Abort()
    {
        abort = true;
        if (threadId != 0) PostThreadMessageW(threadId, 0x12, 0, 0);
        thread.Join(1000);
    }

    public void Dispose()
    {
        // The hook thread checks the deadline inside its own message pump, after pending key notifications.
        if (!PostThreadMessageW(threadId, FinishMessage, 0, 0) || !thread.Join(DdStartupKeys.LimitMs + 1000))
        {
            Abort();
            throw new InvalidOperationException("DD 初始化保护未能正常结束", failure);
        }
        if (failure != null) throw new InvalidOperationException("DD 初始化保护中断", failure);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg { public nint Window; public uint Message; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }
    private delegate nint Callback(int code, nuint message, nint data);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookExW(int type, Callback callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetMessageW(out Msg message, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessageW(out Msg message, nint hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint id, uint message, nuint wp, nint lp);
    [DllImport("user32.dll", SetLastError = true)] private static extern nuint SetTimer(nint hwnd, nuint id, uint ms, nint callback);
    [DllImport("user32.dll")] private static extern bool KillTimer(nint hwnd, nuint id);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? module);
}

// All transitions and deadline checks run on the hook thread. DD hardware input may have no injected flag.
internal sealed class DdStartupKeys(bool leftDown, bool rightDown)
{
    internal const int GraceMs = 2500, QuietMs = 600, LimitMs = 6500;
    private int preheld = (leftDown ? 1 : 0) | (rightDown ? 2 : 0);
    internal int Pending { get; private set; }
    internal int Suppressed { get; private set; }
    internal long LastWin { get; private set; } = -1;

    internal bool Intercept(int key, bool down, long now)
    {
        int bit = key switch { 0x5B => 1, 0x5C => 2, _ => 0 };
        if (bit == 0) return false;
        if ((preheld & bit) != 0)
        {
            // Let the release through if Windows saw the physical press before we installed the hook.
            if (!down) preheld &= ~bit;
            return false;
        }
        Pending = down ? Pending | bit : Pending & ~bit;
        Suppressed++;
        LastWin = now;
        return true;
    }

    internal bool CanFinish(long completed, long now) => completed >= 0 &&
        (now - completed >= LimitMs || now - completed >= GraceMs && Pending == 0 && (LastWin < 0 || now - LastWin >= QuietMs));
}
