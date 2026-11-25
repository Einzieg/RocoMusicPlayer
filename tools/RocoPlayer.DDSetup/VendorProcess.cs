using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RocoPlayer.DDSetup;

/// <summary>Give the vendor process its own log handle, so it can finish writing after a timeout.</summary>
internal sealed class VendorProcess(SafeFileHandle process, int pid) : IVendorProcess
{
    public int Pid => pid;

    public static IVendorProcess Start(string directory, string log)
    {
        var security = new SecurityAttributes { Size = Marshal.SizeOf<SecurityAttributes>(), Inherit = true };
        using var input = CreateFileW("NUL", 0x80000000, 3, ref security, 3, 0, 0);
        if (input.IsInvalid) throw new Win32Exception();
        using var output = CreateFileW(log, 0x40000000, 1, ref security, 1, 0x80, 0);
        if (output.IsInvalid) throw new Win32Exception();
        string executable = Path.Combine(directory, "ddc.exe");
        var startup = new StartupInfo
        {
            Size = Marshal.SizeOf<StartupInfo>(),
            Flags = 0x100, // STARTF_USESTDHANDLES
            Input = input.DangerousGetHandle(),
            Output = output.DangerousGetHandle(),
            Error = output.DangerousGetHandle()
        };
        var command = new StringBuilder().Append('"').Append(executable).Append('"');
        if (!CreateProcessW(executable, command, 0, 0, true, 0x08000000, 0, directory, ref startup, out var process))
            throw new Win32Exception();
        CloseHandle(process.Thread);
        return new VendorProcess(new SafeFileHandle(process.Process, true), process.ProcessId);
    }

    public int? WaitForExit(TimeSpan timeout)
    {
        uint result = WaitForSingleObject(process, checked((uint)timeout.TotalMilliseconds));
        if (result == 0x102) return null;
        if (result != 0 || !GetExitCodeProcess(process, out int code)) throw new Win32Exception();
        return code;
    }

    public void Dispose() => process.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Size; public nint Descriptor; public bool Inherit; }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
        public ushort Show, ReservedSize;
        public nint ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public nint Process, Thread; public int ProcessId, ThreadId; }
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, ref SecurityAttributes security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string exe, StringBuilder command, nint processAttributes,
        nint threadAttributes, bool inherit, uint flags, nint environment, string directory,
        ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeFileHandle handle, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(SafeFileHandle handle, out int code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
