using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RocoPlayer.Playback;

internal static class KeyboardInput
{
    public static IKeySink Prepare(string backend, IEnumerable<string> keys, CancellationToken token) => backend switch
    {
        "Win32" => new Win32Sink(keys),
        "DD" => DdKeyboard.CreateSink(keys, token),
        _ => throw new ArgumentException($"未知输入后端：{backend}")
    };
}

internal sealed class Win32Sink : IKeySink
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Keyboard { public ushort VirtualKey, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Mouse { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Explicit)]
    private struct Payload { [FieldOffset(0)] public Keyboard Key; [FieldOffset(0)] public Mouse Mouse; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public Payload Data; }
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyW(uint key, uint type);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, ref Input input, int size);

    private readonly Dictionary<string, ushort> scanCodes = new();
    public Win32Sink(IEnumerable<string> keys)
    {
        foreach (string key in keys)
        {
            uint code = MapVirtualKeyW(key[0], 0);
            if (code == 0) throw new ArgumentException($"Win32 无法映射键位 {key}");
            scanCodes[key] = checked((ushort)code);
        }
    }
    public void Send(string key, bool down)
    {
        var input = new Input { Type = 1, Data = new Payload { Key = new Keyboard { Scan = scanCodes[key], Flags = down ? 8u : 10u } } };
        Submit(input);
    }

    internal static Action<bool> PrepareClick(bool mouse, int vk)
    {
        if (mouse) return down => Submit(new Input { Data = new Payload { Mouse = new Mouse { Flags = down ? 2u : 4u } } });
        uint scan = MapVirtualKeyW((uint)vk, 4); // MAPVK_VK_TO_VSC_EX returns the extended-key prefix.
        return down =>
        {
            var keyboard = scan == 0
                ? new Keyboard { VirtualKey = (ushort)vk, Flags = down ? 0u : 2u }
                : new Keyboard { Scan = (ushort)(scan & 255), Flags = 8u | ((scan >> 8) == 0xe0 ? 1u : 0u) | (down ? 0u : 2u) };
            Submit(new Input { Type = 1, Data = new Payload { Key = keyboard } });
        };
    }

    private static void Submit(Input input)
    {
        if (SendInput(1, ref input, Marshal.SizeOf<Input>()) != 1)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Win32 输入失败，请检查目标程序与播放器的权限级别");
    }
}

internal sealed class DdKeyboard : IKeySink
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Unary(int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Key(int code, int action);
    private sealed record Session(Unary Translate, Key SendKey, Unary Button);

    private static readonly object Gate = new();
    private static Session? shared;
    private readonly Key send;
    private readonly Dictionary<string, int> codes = new();
    private DdKeyboard(Session driver, IEnumerable<string> keys)
    {
        send = driver.SendKey;
        foreach (string key in keys)
        {
            int code = driver.Translate(key[0]);
            if (code <= 0) throw new InvalidOperationException($"DD 无法映射键位 {key}");
            codes[key] = code;
        }
    }
    public static IKeySink CreateSink(IEnumerable<string> keys, CancellationToken token)
        => WithSession(token, driver => new DdKeyboard(driver, keys));

    internal static Action<bool> PrepareClick(bool mouse, int vk, CancellationToken token) => WithSession<Action<bool>>(token, session =>
    {
        int code = mouse ? 0 : session.Translate(vk);
        if (!mouse && code <= 0) throw new InvalidOperationException("DD 不支持所选按键");
        return down =>
        {
            int direction = down ? 1 : 2;
            int result = mouse ? session.Button(direction) : session.SendKey(code, direction);
            if (result != 1) throw new InvalidOperationException("DD 输入失败");
        };
    });

    private static T WithSession<T>(CancellationToken token, Func<Session, T> prepare)
    {
        lock (Gate)
        {
            token.ThrowIfCancellationRequested();
            if (shared != null) return prepare(shared);
            string path = DdDiagnostics.FindLibrary() ?? throw new FileNotFoundException("未找到 DD DLL。请在「应用设置 → DD 只读诊断」检查驱动位置。");
            DdDiagnostics.Verify(path);
            using var guard = new DdStartupGuard();
            var session = Initialize(path, token);
            // Include the driver's first key translation in the guarded cold start.
            shared = session;
            return prepare(session);
        }
    }

    private static Session Initialize(string path, CancellationToken token)
    {
        nint module = 0;
        try
        {
            token.ThrowIfCancellationRequested();
            module = NativeLibrary.Load(path);
            var begin = Marshal.GetDelegateForFunctionPointer<Unary>(NativeLibrary.GetExport(module, "DD_btn"));
            var session = new Session(
                Marshal.GetDelegateForFunctionPointer<Unary>(NativeLibrary.GetExport(module, "DD_todc")),
                Marshal.GetDelegateForFunctionPointer<Key>(NativeLibrary.GetExport(module, "DD_key")), begin);
            int result = begin(0);
            DdDiagnostics.Log("initialize_attempt", new { attempt = 1, result });
            if (result != 1)
            {
                token.WaitHandle.WaitOne(2000);
                token.ThrowIfCancellationRequested();
                result = begin(0);
                DdDiagnostics.Log("initialize_attempt", new { attempt = 2, result });
            }
            if (result != 1) throw new InvalidOperationException($"DD 初始化失败，返回值 {result}");
            // The native driver and its entry points are shared for the lifetime of the process.
            DdDiagnostics.Log("initialize_ready", new { path });
            return session;
        }
        catch (Exception ex)
        {
            if (module != 0) NativeLibrary.Free(module);
            DdDiagnostics.Log("initialize_failed", new { path, error = ex.Message });
            throw;
        }
    }

    public void Send(string key, bool down)
    {
        if (send(codes[key], down ? 1 : 2) != 1)
            throw new InvalidOperationException($"DD {(down ? "按下" : "释放")} {key} 失败");
    }
}
