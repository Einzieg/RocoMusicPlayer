using System.Diagnostics;
using System.Runtime.InteropServices;
using RocoPlayer.Music;
using RocoPlayer.Playback;

internal static class DdStartupChecks
{
    internal static void CheckTransitions()
    {
        var keys = new DdStartupKeys(false, false);
        Require(!keys.CanFinish(-1, 20000), "初始化未完成时应持续保护");
        Require(!keys.CanFinish(0, 521), "旧延时结束后仍应保护");
        Require(keys.Intercept(0x5B, true, 700) && keys.Intercept(0x5C, true, 2100), "延迟到达的左右 Win 键");
        Require(keys.Intercept(0x5B, false, 2250) && !keys.CanFinish(0, 3100), "仍有右 Win 键待释放");
        Require(keys.Intercept(0x5C, false, 3200) && !keys.CanFinish(0, 3201), "释放后仍应等待输入稳定");
        Require(keys.Suppressed == 4 && keys.Pending == 0 && keys.CanFinish(0, 3200 + DdStartupKeys.QuietMs), "成对释放后退出");
        foreach (int key in new[] { 0x42, 0x77, 0x78, 0x79 })
            Require(!keys.Intercept(key, true, 3210) && !keys.Intercept(key, false, 3220), "演奏键和 F8/F9/F10 应通行");
        var held = new DdStartupKeys(true, false);
        Require(!held.Intercept(0x5B, true, 10) && !held.Intercept(0x5B, false, 20), "保护前已按住的 Win 键可正常释放");
        Require(held.Intercept(0x5B, true, 30) && !held.CanFinish(0, DdStartupKeys.LimitMs - 1) &&
            held.CanFinish(0, DdStartupKeys.LimitMs), "释放事件丢失时有结束期限");
        Require(new DdStartupKeys(false, false).CanFinish(100, 100 + DdStartupKeys.GraceMs), "没有 Win 键时按时退出");
    }

    // Run in a new process on a machine with DD installed. Preparation maps keys without performing a score.
    internal static async Task CheckLocal()
    {
        string[] keys = new PlayerSettings().KeyMap.Values.ToArray();
        int samples = 0;
        void Observe()
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out int pid);
            if (pid == 0) return;
            try
            {
                using var process = Process.GetProcessById(pid);
                Require(!process.ProcessName.Equals("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase),
                    "DD 初始化前后检测到开始菜单位于前台");
                samples++;
            }
            catch (ArgumentException) { /* The foreground process exited before it could be queried. */ }
        }
        Observe();
        var preparation = Task.Run(() => KeyboardInput.Prepare("DD", keys, default)).WaitAsync(TimeSpan.FromSeconds(30));
        while (!preparation.IsCompleted) { Observe(); await Task.Delay(25); }
        await preparation;
        KeyboardInput.Prepare("DD", keys, default);
        var after = Stopwatch.StartNew();
        while (after.ElapsedMilliseconds < 1200) { Observe(); await Task.Delay(25); }
        Require(samples > 0, "未能观测到前台窗口");
        Console.WriteLine($"PASS DD 冷启动、九键映射及会话复用，进程 {Environment.ProcessId}，{samples} 次前台采样均未出现开始菜单");
    }

    private static void Require(bool ok, string label) { if (!ok) throw new InvalidOperationException(label); }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out int pid);
}
