using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using RocoPlayer;
using RocoPlayer.Music;
using RocoPlayer.Playback;

namespace RocoPlayer.Calibrate;

internal static class Program
{
    private const string Usage = """
        RocoPlayer.Calibrate --hwnd <十进制或 0x 句柄> [--wait-foreground 秒] [--focus] [--delay 秒] [--output 目录]
        使用 DD 在指定游戏窗口实测当前九键映射。倒计时默认 3 秒。F10 或 Ctrl+C 取消。
        --wait-foreground 等待目标连续处于前台一秒。--focus 请求切换到目标窗口。
        WAV 和 measurements.json 默认写入 artifacts/calibration 下的时间目录。
        """;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args is ["--help"] or ["-h"]) { Console.WriteLine(Usage); return 0; }
        try
        {
            string? output = null;
            double wait = 0, delay = 3;
            long handle = 0;
            bool focus = false;
            for (int i = 0; i < args.Length; i++)
            {
                string option = args[i];
                string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"{option} 缺少参数值");
                switch (option)
                {
                    case "--hwnd":
                        string raw = Next();
                        handle = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                            ? long.Parse(raw[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
                            : long.Parse(raw, CultureInfo.InvariantCulture);
                        break;
                    case "--focus": focus = true; break;
                    case "--output": output = Path.GetFullPath(Next()); break;
                    case "--delay": delay = Seconds(Next()); break;
                    case "--wait-foreground": wait = Seconds(Next()); break;
                    default: throw new ArgumentException($"未知参数 {option}");
                }
            }
            if (handle <= 0) throw new ArgumentException("请通过 --hwnd 指定游戏窗口句柄");
            var target = TargetWindow.FromHandle(checked((nint)handle));
            string home = Environment.GetEnvironmentVariable("ROCK_MUSIC_HOME") ?? Environment.CurrentDirectory;
            var folder = Environment.GetEnvironmentVariable("ROCK_MUSIC_DATA_DIR");
            if (string.IsNullOrWhiteSpace(folder)) folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "com.rockmusic.workspace-player");
            var library = new ScoreLibrary(folder, Path.Combine(home, "music_settings.json"));
            var settings = library.Settings with { Backend = "DD" };
            output ??= Path.Combine(home, "artifacts", "calibration", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            using var stop = new CancellationTokenSource();
            ConsoleCancelEventHandler ctrlC = (_, e) => { e.Cancel = true; stop.Cancel(); };
            Console.CancelKeyPress += ctrlC;
            var monitor = MonitorStop(stop);
            using var player = new PlaybackEngine();
            try
            {
                Console.WriteLine($"目标：{target.Title}。按 F10 或 Ctrl+C 停止。");
                if (wait > 0) await WaitForeground(target, wait, stop.Token);
                if (focus) await target.ActivateAsync(stop.Token);
                if (delay > 0) { Console.WriteLine($"{delay} 秒后开始，请切到目标窗口"); await Task.Delay(TimeSpan.FromSeconds(delay), stop.Token); }
                if (target.State != "ready") throw new InvalidOperationException("目标窗口未处于前台");
                await player.PrepareAsync("DD", settings.KeyMap.Values, target, stop.Token);
                await Calibration.MeasureAsync(settings, player, target, output, new ConsoleProgress(), stop.Token);
                Console.WriteLine($"测量结果：{output}");
                return 0;
            }
            finally
            {
                stop.Cancel();
                Console.CancelKeyPress -= ctrlC;
                await monitor;
                await player.StopAsync();
            }
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("已取消测量"); return 130; }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 2; }
    }

    private static double Seconds(string text)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
            !double.IsFinite(seconds) || seconds < 0 || seconds > int.MaxValue / 1000d)
            throw new ArgumentException("秒数超出范围");
        return seconds;
    }

    private static async Task WaitForeground(TargetWindow window, double seconds, CancellationToken token)
    {
        Console.WriteLine("等待目标窗口连续处于前台一秒…");
        var clock = Stopwatch.StartNew();
        double? ready = null;
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            var state = window.State;
            if (state == "closed") throw new InvalidOperationException("目标窗口已关闭");
            double now = clock.Elapsed.TotalSeconds;
            ready = state == "ready" ? ready ?? now : null;
            if (now - ready >= 1) return;
            await Task.Delay(20, token);
        }
        throw new TimeoutException("等待目标窗口处于前台超时");
    }

    private static async Task MonitorStop(CancellationTokenSource stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                if (GetAsyncKeyState(0x79) < 0) { stop.Cancel(); break; }
                await Task.Delay(10, stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    private sealed class ConsoleProgress : IProgress<string> { public void Report(string message) => Console.WriteLine(message); }
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
}
