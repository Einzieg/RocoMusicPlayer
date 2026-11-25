using RocoPlayer.DDSetup;
using RocoPlayer.Playback;

try
{
    string? driver = null, logs = null;
    var mode = SetupMode.Install;
    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--dd-setup": break;
            case "--inspect-only": mode = SetupMode.Inspect; break;
            case "--check-install": mode = SetupMode.CheckInstall; break;
            case "--driver-dir" when i + 1 < args.Length: driver = Path.GetFullPath(args[++i]); break;
            case "--log-dir" when i + 1 < args.Length: logs = Path.GetFullPath(args[++i]); break;
            default: throw new ArgumentException($"未知参数或缺少值：{args[i]}");
        }
    }
    if (driver is null || logs is null)
        throw new ArgumentException("用法：RocoPlayer.DDSetup --driver-dir <驱动目录> --log-dir <日志目录> [--inspect-only | --check-install]");
    var setup = new DriverSetup(DdDeviceInspector.Inspect, VendorProcess.Start, DriverSetup.VerifyFiles, Thread.Sleep);
    return setup.Run(driver, logs, mode);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"DD 安装检查失败：{ex.Message}");
    return 2;
}
