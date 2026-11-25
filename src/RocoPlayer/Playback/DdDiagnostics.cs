using static RocoPlayer.Playback.DdPackage;
using Microsoft.Win32;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RocoPlayer.Playback;

public static class DdDiagnostics
{
    private static readonly object LogGate = new();
    private static readonly string LogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RocoPlayer", "logs", "dd-runtime.jsonl");

    public static string? FindLibrary()
    {
        string? custom = Environment.GetEnvironmentVariable("DD_DLL_PATH");
        if (!string.IsNullOrWhiteSpace(custom)) return Path.GetFullPath(custom);
        foreach (var root in new[] { AppContext.BaseDirectory, Environment.GetEnvironmentVariable("ROCK_MUSIC_HOME") })
        {
            if (root is null) continue;
            foreach (var candidate in new[] { $"drivers/dd/{Bundled}", $"drivers/{Bundled}", $"DD/{Bundled}", Bundled,
                "DD/64/ddx64.64.dll", "DD/64/ddx64.32.dll", "DD/32/ddx32.dll", $"2026.DD.EV.HVCI.63xxx/2.hid/{Bundled}",
                "2026.DD.EV.HVCI.63xxx/1.simple/dd63330.dll" })
            {
                var path = Path.GetFullPath(Path.Combine(root, candidate));
                if (File.Exists(path)) return path;
            }
        }
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
                if (FindInstalledLibrary(hive, view) is string path) return path;
        return null;
    }

    private static string? FindInstalledLibrary(RegistryHive hive, RegistryView view)
    {
        try
        {
            using var registry = RegistryKey.OpenBaseKey(hive, view);
            foreach (string app in new[] { "RocoPlayerWPF", "RocoPlayer" })
            {
                using var entry = registry.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{app}");
                string? root = (entry?.GetValue("InstallLocation") as string)?.Trim().Trim('"');
                if (string.IsNullOrEmpty(root) && entry?.GetValue("DisplayIcon") is string icon)
                    root = Path.GetDirectoryName(icon.Trim().Trim('"'));
                if (string.IsNullOrEmpty(root) || !Path.IsPathFullyQualified(root)) continue;
                string path = Path.Combine(root, "drivers", "dd", Bundled);
                if (File.Exists(path)) return path;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
        {
            System.Diagnostics.Trace.TraceWarning(ex.Message);
        }
        return null;
    }

    private static string Check(string path, string? expected)
    {
        if (!File.Exists(path)) return "缺失";
        using var file = File.OpenRead(path);
        string hash = Convert.ToHexString(SHA256.HashData(file));
        return expected == null ? $"SHA256 {hash}（无校验基准）" :
            hash.Equals(expected, StringComparison.OrdinalIgnoreCase) ? "校验通过" : "校验不匹配";
    }

    internal static void Verify(string path)
    {
        bool known = Hashes.TryGetValue(Path.GetFileName(path), out var expected);
        string result = Check(path, expected);
        Log("library_check", new { path, result });
        if (result == "缺失" || (known && result != "校验通过"))
            throw new InvalidDataException($"DD DLL {result}：{path}");
    }

    internal static bool Log(string action, object detail)
    {
        try
        {
            lock (LogGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                if (new FileInfo(LogPath) is { Exists: true, Length: >= 1048576 })
                {
                    if (File.Exists(LogPath + ".1")) File.Move(LogPath + ".1", LogPath + ".2", true);
                    File.Move(LogPath, LogPath + ".1", true);
                }
                var line = JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow, pid = Environment.ProcessId, @event = action, detail });
                File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning(ex.Message);
            return false;
        }
    }

    public static string Inspect()
    {
        var report = new StringBuilder();
        report.AppendLine("DD 只读诊断");
        try
        {
            string? path = FindLibrary();
            if (path is null) report.AppendLine("未找到本机的 DD DLL。请安装带 DD 的 RocoPlayer，或用 DD_DLL_PATH 指定已有的 DLL。");
            else
            {
                string name = Path.GetFileName(path);
                report.AppendLine($"{path}　{Check(path, Hashes.GetValueOrDefault(name))}");
                if (name.Equals(Bundled, StringComparison.OrdinalIgnoreCase))
                {
                    string directory = Path.GetDirectoryName(path)!;
                    foreach (var (file, expected) in Hashes.Where(pair => pair.Key.StartsWith("drv/")))
                        report.AppendLine($"{file}　{Check(Path.Combine(directory, file), expected)}");
                    foreach (var dll in Runtimes)
                        report.AppendLine($"drv/{dll}　{(File.Exists(Path.Combine(directory, "drv", dll)) ? "存在" : "缺失")}");
                }
            }
        }
        catch (Exception ex) { report.AppendLine($"文件检查失败：{ex.Message}"); }
        try
        {
            var service = DdDeviceInspector.InspectService();
            report.AppendLine(service.Registered
                ? $"{DdDeviceInspector.Service} 服务状态：{(service.State == 4 ? "运行中" : service.State.ToString())}；退出码 {service.Win32Exit}/{service.ServiceExit}"
                : $"{DdDeviceInspector.Service} 服务未注册");
        }
        catch (Exception ex) { report.AppendLine($"驱动服务查询失败：{ex.Message}"); }
        try
        {
            var devices = DdDeviceInspector.InspectDevices();
            if (devices.Length == 0) report.AppendLine("未找到对应的 PnP 设备");
            foreach (var device in devices)
                report.AppendLine(device.QueryResult == 0
                    ? $"{device.Id}　{(device.Started ? "已启动" : "未启动")}；问题码 {device.Problem}"
                    : $"{device.Id}　查询结果 0x{device.QueryResult:X}");
        }
        catch (Exception ex) { report.AppendLine($"PnP 查询失败：{ex.Message}"); }
        var installLogs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RocoPlayer", "logs");
        if (File.Exists(Path.Combine(installLogs, "dd-install-pending.json")))
            report.AppendLine($"存在安装超时标记，请检查安装日志：{installLogs}");
        report.AppendLine(Log("diagnostics", new { report = report.ToString() }) ? $"诊断日志：{LogPath}" : "无法写入诊断日志");
        return report.ToString();
    }
}
