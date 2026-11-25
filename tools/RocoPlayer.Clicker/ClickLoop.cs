using System.ComponentModel;
using System.Runtime.InteropServices;
using RocoPlayer.Playback;

namespace RocoPlayer.Clicker;

internal enum SendMode { Win32, DD, DDTarget, WindowMessage }
internal sealed record ClickOptions(int IntervalMs, bool Mouse, int Vk, char? Character, SendMode Mode, TargetWindow? Target)
{
    public void Validate(int hotkey)
    {
        if (IntervalMs < 0) throw new ArgumentException("请输入非负整数毫秒数");
        if (!Mouse && (Vk is < 1 or > 254 || Vk == hotkey))
            throw new ArgumentException("请选择有效目标按键，并与启停热键区分");
        if (Mode is SendMode.DDTarget or SendMode.WindowMessage)
        {
            if (Mouse) throw new ArgumentException("目标窗口模式只支持键盘");
            if (Target is null || Target.State == "closed") throw new ArgumentException("请先捕获目标窗口");
        }
    }

    public Action<bool> Prepare(CancellationToken token) => Mode switch
    {
        SendMode.Win32 => Win32Sink.PrepareClick(Mouse, Vk),
        SendMode.DD or SendMode.DDTarget => DdKeyboard.PrepareClick(Mouse, Vk, token),
        SendMode.WindowMessage => PrepareMessage(),
        _ => throw new ArgumentException("未知输入后端")
    };

    public bool CanSend()
    {
        if (Mode is not (SendMode.DDTarget or SendMode.WindowMessage)) return true;
        var state = Target!.State;
        if (state == "closed") throw new InvalidOperationException("目标窗口已关闭，请重新捕获");
        return Mode == SendMode.WindowMessage || state == "ready";
    }

    private Action<bool> PrepareMessage()
    {
        var window = Target!;
        uint scan = MapVirtualKeyW((uint)Vk, 4);
        uint bits = 1 | ((scan & 0xff) << 16) | ((scan >> 8) == 0xe0 ? 1u << 24 : 0);
        void Post(uint message, int value, uint flags)
        {
            if (!PostMessageW(window.Handle, message, (nuint)value, unchecked((nint)(int)flags)))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "目标窗口消息发送失败");
        }
        return down =>
        {
            // A closed destination no longer owns a pressed key. Never deliver its key-up to a reused HWND.
            if (window.State == "closed")
            {
                if (down) throw new InvalidOperationException("目标窗口已关闭");
                return;
            }
            Post(down ? 0x100u : 0x101u, Vk, down ? bits : bits | 0xc0000000);
            if (down && Character is char c) Post(0x102, c, bits);
        };
    }

    [DllImport("user32.dll")] private static extern uint MapVirtualKeyW(uint code, uint type);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessageW(nint hwnd, uint message, nuint key, nint data);
}

internal sealed class ClickLoop
{
    private Action<bool>? outstanding;
    public bool NeedsRelease => outstanding != null;

    // Exactly one worker owns press/release. The UI retries only once that worker has stopped.
    public void Run(Action<bool> send, Func<bool> allowed, int interval, int holdMs, CancellationToken token)
    {
        if (NeedsRelease) throw new InvalidOperationException("请先停止并释放上一轮按键");
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (allowed())
                {
                    token.ThrowIfCancellationRequested();
                    outstanding = send;
                    send(true);
                    if (holdMs > 0) token.WaitHandle.WaitOne(holdMs);
                    send(false);
                    outstanding = null;
                }
                if (interval > 0) token.WaitHandle.WaitOne(interval);
                else Thread.Yield();
            }
        }
        finally { RetryRelease(); }
    }

    public void RetryRelease()
    {
        var release = outstanding;
        if (release == null) return;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try { release(false); outstanding = null; return; }
            catch (Exception) when (attempt < 2) { }
        }
    }
}
