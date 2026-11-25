using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace RocoPlayer.Playback;

/// <summary>Construct and dispose on the desktop UI dispatcher.</summary>
public sealed class GlobalHotkeys : IDisposable
{
    private const int Play = 0x77, Pause = 0x78, Stop = 0x79;
    private readonly Action<string> dispatch;
    private readonly HashSet<(nint Device, int Key)> held = new();
    private readonly HashSet<int> startup;
    private HashSet<int> previous;
    private HwndSource? window;
    private DispatcherTimer? timer;
    public string Mode { get; private set; } = "raw-input";

    public GlobalHotkeys(Action<string> callback)
    {
        dispatch = callback;
        startup = ReadPhysicalKeys();
        previous = new(startup);
        try
        {
            window = new HwndSource(new HwndSourceParameters("RocoPlayer transport")
            {
                ParentWindow = new nint(-3), Width = 0, Height = 0, WindowStyle = 0
            });
            window.AddHook(HandleMessage);
            var device = new RawDevice { UsagePage = 1, Usage = 6, Flags = 0x100 | 0x2000, Window = window.Handle };
            if (!RegisterRawInputDevices(ref device, 1, (uint)Marshal.SizeOf<RawDevice>()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"全局快捷键已切换为轮询：{ex.Message}");
            window?.Dispose();
            window = null;
            Mode = "polling";
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
            timer.Tick += Poll;
            timer.Start();
        }
    }

    private static HashSet<int> ReadPhysicalKeys() =>
        new[] { Stop, Pause, Play }.Where(k => GetAsyncKeyState(k) < 0).ToHashSet();
    private void Poll(object? sender, EventArgs args) => Update(ReadPhysicalKeys());

    private void Update(HashSet<int> down)
    {
        var old = previous;
        previous = down;
        if (down.Contains(Stop))
        {
            if (!old.Contains(Stop)) dispatch("stop");
            return;
        }
        if (down.Contains(Pause) && !old.Contains(Pause)) dispatch("pause");
        else if (down.Contains(Play) && !old.Contains(Play)) dispatch("play");
    }

    private nint HandleMessage(nint hwnd, int msg, nint wp, nint lp, ref bool handled)
    {
        if (msg == 0x00FE && wp == 2) // WM_INPUT_DEVICE_CHANGE, GIDC_REMOVAL
            held.RemoveWhere(entry => entry.Device == lp);
        else if (msg == 0x00FF)
        {
            uint size = (uint)Marshal.SizeOf<RawPacket>();
            if (GetRawInputData(lp, 0x10000003, out var packet, ref size, (uint)Marshal.SizeOf<RawHeader>()) == uint.MaxValue ||
                packet.Header.Type != 1 || packet.Key.VKey is not (Play or Pause or Stop)) return 0;
            int key = packet.Key.VKey;
            if ((packet.Key.Flags & 1) == 0) held.Add((packet.Header.Device, key));
            else
            {
                held.Remove((packet.Header.Device, key));
                startup.Remove(key);
            }
        }
        else return 0;
        Update(held.Select(e => e.Key).Concat(startup).ToHashSet());
        return 0;
    }

    public void Dispose()
    {
        if (timer != null)
        {
            timer.Stop();
            timer.Tick -= Poll;
            timer = null;
        }
        if (window != null)
        {
            var device = new RawDevice { UsagePage = 1, Usage = 6, Flags = 1 };
            RegisterRawInputDevices(ref device, 1, (uint)Marshal.SizeOf<RawDevice>());
            window.RemoveHook(HandleMessage);
            window.Dispose();
            window = null;
        }
        Mode = "stopped";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawDevice { public ushort UsagePage, Usage; public uint Flags; public nint Window; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RawHeader { public uint Type, Size; public nint Device; public nuint Param; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RawKey { public ushort MakeCode, Flags, Reserved, VKey; public uint Message, Extra; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RawPacket { public RawHeader Header; public RawKey Key; }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(ref RawDevice devices, uint count, uint size);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] private static extern uint GetRawInputData(nint data, uint command, out RawPacket packet, ref uint size, uint headerSize);
}
