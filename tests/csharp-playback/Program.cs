using System.Collections.Concurrent;
using RocoPlayer.Music;
using RocoPlayer.Playback;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static async Task Until(Func<bool> condition)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (!condition()) await Task.Delay(5, timeout.Token);
}

if (args.Contains("--dd-cold-start")) { await DdStartupChecks.CheckLocal(); return; }
DdStartupChecks.CheckTransitions();
var score = new Score("测试", [new(0, .2, 60), new(0, .2, 60), new(.08, .12, 60), new(0, .2, 64)], .20);
var keys = new Dictionary<int,string> { [60] = "F", [64] = "G" };
var plan = PlaybackPlan.Compile(score, keys, 1, 150);
Check(plan.Events.Length == 6, "重复起音应合并，和弦应保留");
Check(Math.Abs(plan.Events.First(e => e.Key == "F" && !e.Down).Time - .064) < 1e-9, "连击间应释放");
var offsetPlan = PlaybackPlan.Compile(score, keys, 2, 150, .1);
Check(offsetPlan.Events.Count(e => e.Down && e.Time == 0) == 2 && Math.Abs(offsetPlan.Duration - .05) < 1e-9,
    "从和弦中间播放时保留余下的音符并按速度缩放");
var rest = PlaybackPlan.Compile(score with { Duration = 1 }, keys, 1, 150, .6);
Check(rest.Events.Length == 0 && Math.Abs(rest.Duration - .4) < 1e-9, "从末尾休止处播放");
HandpanChecks.Run();

using var engine = new PlaybackEngine();
await engine.PlayAsync(score, keys, "missing", null, preview: true, delay: 0).WaitAsync(TimeSpan.FromSeconds(5));
Check(engine.Status.State == "finished" && engine.Status.Pressed.Length == 0, "无设备预览未完成");

var longPlan = new PlaybackPlan([new(0, "F", true), new(2, "F", false)], 2);
var sink = new FakeSink();
string focus = "ready";
var playing = engine.StartPerformance(_ => new(longPlan, sink, () => Volatile.Read(ref focus)), 0, false, default);
await Until(() => sink.Events.Contains(("F", true)));
Volatile.Write(ref focus, "background");
await Until(() => engine.Status.State == "paused");
Check(sink.Events.Last() == ("F", false) && engine.Status.Pressed.Length == 0, "失焦应释放按键");
Volatile.Write(ref focus, "ready");
await Task.Delay(60);
Check(engine.Status.State == "paused", "回到游戏后应等待人工继续");
engine.TogglePause();
await Until(() => engine.Status.State == "playing");
await engine.StopAsync();
await playing;
Check(engine.Status.State == "stopped", "停止未生效");

var broken = new FakeSink { FailRelease = true };
playing = engine.StartPerformance(_ => new(plan, broken, () => "ready"), 0, false, default);
try { await playing; throw new Exception("应报告释放失败"); } catch (Exception) when (engine.Status.State == "error") { }
Check(engine.Status.Pressed.Length > 0, "释放失败的按键应保留");
try { await engine.PlayAsync(score, keys, "missing", null, preview: true); throw new Exception("释放失败后不应接受新播放"); }
catch (InvalidOperationException) { }
broken.FailRelease = false;
await engine.StopAsync();
Check(engine.Status.Pressed.Length == 0 && engine.Status.State == "stopped", "停止应重试失败的释放");

using var preparing = new ManualResetEventSlim();
using var resume = new ManualResetEventSlim();
var late = new FakeSink();
playing = engine.StartPerformance(_ =>
{
    preparing.Set();
    resume.Wait();
    return new(plan, late, () => "ready");
}, 0, false, default);
Check(preparing.Wait(5000), "准备超时");
engine.RequestStop();
resume.Set();
await playing;
Check(late.Events.IsEmpty && engine.Status.State == "stopped", "准备中停止后不能补发按键");

// Registration uses a hidden WPF message-only window. Never synthesize a hotkey in this check.
Exception? windowError = null;
var dispatcher = new Thread(() =>
{
    try
    {
        using var keys = new GlobalHotkeys(_ => { });
        Check(keys.Mode == "raw-input", "Raw Input 注册失败");
    }
    catch (Exception ex) { windowError = ex; }
});
dispatcher.SetApartmentState(ApartmentState.STA);
dispatcher.Start();
Check(dispatcher.Join(TimeSpan.FromSeconds(45)), "WPF 快捷键检查线程启动或退出超时");
if (windowError != null) throw new InvalidOperationException("WPF 快捷键注册失败", windowError);
Console.WriteLine("通过：连击、和弦、无按键预览、失焦暂停与释放、手动恢复、停止重试、准备中取消、WPF 后台快捷键注册。");

internal sealed class FakeSink : IKeySink
{
    public ConcurrentQueue<(string Key, bool Down)> Events { get; } = new();
    public volatile bool FailRelease;
    public void Send(string key, bool down)
    {
        if (!down && FailRelease) throw new InvalidOperationException("模拟后端释放失败");
        Events.Enqueue((key, down));
    }
}
