using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RocoPlayer.Playback;

internal sealed record DriverService(bool Registered, uint State, uint Win32Exit, uint ServiceExit);
internal sealed record Device(string Id, uint QueryResult, uint Status, uint Problem)
{
    public bool Started => (Status & 8) != 0;
}
internal sealed record DriverState(DriverService Service, Device[] Devices)
{
    public bool Healthy => Service.Registered && Service.State == 4 && Devices.Length > 0 &&
        Devices.All(device => device.QueryResult == 0 && device.Started && device.Problem == 0);
}
internal static class DdDeviceInspector
{
    internal const string Service = "ddhid63340";
    public static DriverState Inspect() => new(InspectService(), InspectDevices());
    public static DriverService InspectService()
    {
        nint scm = OpenSCManagerW(null, null, 1);
        if (scm == 0) throw new Win32Exception();
        try
        {
            nint handle = OpenServiceW(scm, Service, 4);
            if (handle == 0)
            {
                if (Marshal.GetLastWin32Error() != 1060) throw new Win32Exception();
                return new(false, 0, 0, 0);
            }
            try
            {
                if (!QueryServiceStatus(handle, out var state)) throw new Win32Exception();
                return new(true, state.State, state.Win32Exit, state.ServiceExit);
            }
            finally { CloseServiceHandle(handle); }
        }
        finally { CloseServiceHandle(scm); }
    }

    public static Device[] InspectDevices()
    {
        const uint presentByService = 0x102;
        char[] ids = [];
        uint rc = 0;
        for (int i = 0; i < 3; i++)
        {
            rc = CM_Get_Device_ID_List_SizeW(out var length, Service, presentByService);
            if (rc != 0) break;
            ids = new char[Math.Max(2, length)];
            rc = CM_Get_Device_ID_ListW(Service, ids, (uint)ids.Length, presentByService);
            if (rc != 0x1a) break; // Device list changed between the two calls.
        }
        if (rc != 0) throw new InvalidOperationException($"设备列表返回 0x{rc:X}");
        var devices = new string(ids).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<Device>();
        foreach (string device in devices)
        {
            rc = CM_Locate_DevNodeW(out var node, device, 0);
            uint status = 0, problem = 0;
            if (rc == 0) rc = CM_Get_DevNode_Status(out status, out problem, node, 0);
            result.Add(new(device, rc, status, problem));
        }
        return result.ToArray();
    }

    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus { public uint Type, State, Accepted, Win32Exit, ServiceExit, Checkpoint, WaitHint; }
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern nint OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern nint OpenServiceW(nint scm, string service, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatus(nint handle, out ServiceStatus status);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(nint handle);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Get_Device_ID_List_SizeW(out uint length, string filter, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Get_Device_ID_ListW(string filter, [Out] char[] ids, uint length, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern uint CM_Locate_DevNodeW(out uint node, string id, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint node, uint flags);
}
