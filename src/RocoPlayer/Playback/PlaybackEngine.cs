using System.Diagnostics;
using RocoPlayer.Music;

namespace RocoPlayer.Playback;

public sealed record PlaybackStatus(string State, double Position, double Duration, string Message, string[] Pressed);

internal interface IKeySink
{
    void Send(string key, bool down);
}

internal sealed record PreparedPerformance(PlaybackPlan Plan, IKeySink? Sink, Func<string> TargetState);

public sealed class PlaybackEngine : IDisposable
{
    private readonly object gate = new();
    private CancellationTokenSource? stop;
    private Task worker = Task.CompletedTask;
    private volatile bool paused;
    private bool disposed;
    // Only the active worker mutates held keys. StopAsync retries failed releases after joining it.
    private readonly HashSet<string> held = new();
    private IKeySink? sink;
    private volatile PlaybackStatus status = new("idle", 0, 0, "就绪", []);
    public PlaybackStatus Status => status;
    public bool IsRunning { get { lock (gate) return !worker.IsCompleted; } }

    public Task PrepareAsync(string backend, IEnumerable<string> keys, TargetWindow? target, CancellationToken token)
    {
        if (target == null || target.State == "closed") throw new InvalidOperationException("请先捕获游戏窗口");
        var selected = keys.Select(PlaybackPlan.NormalizeKey).Distinct().ToArray();
        return StartWorker(stopToken =>
        {
            try
            {
                KeyboardInput.Prepare(backend, selected, stopToken);
                stopToken.ThrowIfCancellationRequested();
                if (target.State == "closed") throw new InvalidOperationException("目标窗口已关闭，请重新捕获");
                Publish("idle", 0, 0, "输入后端已就绪");
            }
            catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
            {
                Publish("stopped", 0, 0, "已停止");
                throw;
            }
            catch (Exception ex)
            {
                Publish("error", 0, 0, ex.Message);
                throw;
            }
        }, token);
    }

    public Task PlayAsync(Score score, IReadOnlyDictionary<int, string> mapping, string backend, TargetWindow? target,
        bool preview = false, double speed = 1, double holdMs = 60, double delay = 3,
        CancellationToken cancellationToken = default, bool activate = true, double startPosition = 0)
    {
        if (!preview && (target is null || target.State == "closed"))
            throw new InvalidOperationException("请先捕获游戏窗口");
        if (!double.IsFinite(delay) || delay is < 0 or > 30)
            throw new ArgumentException("倒计时必须在 0～30 秒之间");

        return StartPerformance(token =>
        {
            var plan = PlaybackPlan.Compile(score, mapping, speed, holdMs, startPosition);
            token.ThrowIfCancellationRequested();
            var input = preview ? null : KeyboardInput.Prepare(backend, plan.Events.Select(e => e.Key).Distinct(), token);
            token.ThrowIfCancellationRequested();
            if (!preview && activate) target!.EnsureForeground(token);
            if (!preview && target!.State != "ready") throw new InvalidOperationException("请切回游戏窗口");
            return new(plan, input, preview ? () => "ready" : () => target!.State);
        }, delay, false, cancellationToken);
    }

    public Task TapAsync(string key, string backend, TargetWindow target, double holdMs, CancellationToken cancellationToken)
    {
        key = PlaybackPlan.NormalizeKey(key);
        if (!double.IsFinite(holdMs) || holdMs is <= 0 or > 2000) throw new ArgumentOutOfRangeException(nameof(holdMs));
        return StartPerformance(token =>
        {
            if (target.State != "ready") throw new InvalidOperationException("校准前请切回游戏窗口");
            var input = KeyboardInput.Prepare(backend, [key], token);
            double duration = holdMs / 1000;
            var plan = new PlaybackPlan([new(0, key, true), new(duration, key, false)], duration);
            return new(plan, input, () => target.State);
        }, 0, true, cancellationToken);
    }

    internal Task StartPerformance(Func<CancellationToken, PreparedPerformance> prepare, double delay,
        bool calibration, CancellationToken cancellationToken) =>
        StartWorker(token => Perform(prepare, delay, calibration, token), cancellationToken);

    private Task StartWorker(Action<CancellationToken> action, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!worker.IsCompleted || held.Count != 0) throw new InvalidOperationException("请先停止上一轮演奏并释放按键");
            cancellationToken.ThrowIfCancellationRequested();
            stop?.Dispose();
            stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = stop.Token;
            paused = false;
            sink = null;
            Publish("countdown", 0, 0, "准备演奏");
            worker = Task.Run(() => action(token));
            return worker;
        }
    }

    private void Perform(Func<CancellationToken, PreparedPerformance> prepare, double delay, bool calibration, CancellationToken token)
    {
        Exception? failure = null;
        try
        {
            token.ThrowIfCancellationRequested();
            var performance = prepare(token);
            sink = performance.Sink;
            Run(performance.Plan, performance.TargetState, delay, token, calibration);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { failure = ex; }
        finally
        {
            try { ReleaseAll(); }
            catch (Exception ex) { failure = failure == null ? ex : new AggregateException(failure, ex); }
            if (failure != null) Publish("error", status.Position, status.Duration, failure.Message);
            else if (token.IsCancellationRequested) Publish("stopped", status.Position, status.Duration, "已停止，按键已释放");
            else Publish("finished", status.Duration, status.Duration, "演奏完成");
        }
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        if (calibration) token.ThrowIfCancellationRequested();
    }

    private void Run(PlaybackPlan plan, Func<string> targetState, double delay, CancellationToken token, bool calibration)
    {
        int index = 0;
        double position = 0;
        var clock = Stopwatch.StartNew();
        double last = 0;
        string reason = "已暂停，按 F9 继续";
        while (true)
        {
            token.ThrowIfCancellationRequested();
            double now = clock.Elapsed.TotalSeconds, elapsed = now - last;
            last = now;
            if (paused)
            {
                if (calibration) throw new InvalidOperationException("校准已中断");
                ReleaseAll();
                if (targetState() == "closed") throw new InvalidOperationException("目标窗口已关闭，请重新捕获");
                Publish("paused", position, plan.Duration, reason);
            }
            else if (delay > 0)
            {
                delay = Math.Max(0, delay - elapsed);
                Publish("countdown", position, plan.Duration, $"{Math.Ceiling(delay)} 秒后开始");
            }
            else
            {
                string state = targetState();
                if (state == "closed") throw new InvalidOperationException("目标窗口已关闭，请重新捕获");
                if (state != "ready" || elapsed > 0.25)
                {
                    reason = state != "ready" ? "游戏窗口失焦，已暂停；回到游戏后按 F9 继续" : "系统响应延迟，已暂停；按 F9 继续";
                    paused = true;
                    continue;
                }
                reason = "已暂停，按 F9 继续";
                position = Math.Min(plan.Duration, position + elapsed);
                while (index < plan.Events.Length && plan.Events[index].Time <= position)
                {
                    token.ThrowIfCancellationRequested();
                    if (paused) break;
                    var change = plan.Events[index];
                    if (change.Down)
                    {
                        if (targetState() != "ready")
                        {
                            paused = true;
                            reason = "游戏窗口失焦，已暂停；回到游戏后按 F9 继续";
                            break;
                        }
                        token.ThrowIfCancellationRequested();
                        held.Add(change.Key);
                        sink?.Send(change.Key, true);
                    }
                    else if (held.Contains(change.Key))
                    {
                        sink?.Send(change.Key, false);
                        held.Remove(change.Key);
                    }
                    index++;
                }
                Publish("playing", position, plan.Duration, "演奏中 · F9 暂停 · F10 停止");
                if (index == plan.Events.Length && position >= plan.Duration) return;
            }
            token.WaitHandle.WaitOne(4);
        }
    }

    private void Publish(string state, double position, double duration, string message) =>
        status = new(state, position, duration, message, held.ToArray());

    private void ReleaseAll()
    {
        foreach (string key in held.ToArray())
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    sink?.Send(key, false);
                    held.Remove(key);
                    break;
                }
                catch (Exception) { /* Other keys must still receive their releases. */ }
            }
        }
        if (held.Count > 0) throw new InvalidOperationException($"释放按键失败，请再次停止：{string.Join(", ", held)}");
    }

    public void TogglePause()
    {
        lock (gate) if (!worker.IsCompleted && stop?.IsCancellationRequested == false) paused = !paused;
    }

    public void SetPaused(bool value)
    {
        lock (gate) if (!worker.IsCompleted && stop?.IsCancellationRequested == false) paused = value;
    }

    public void RequestStop() { lock (gate) stop?.Cancel(); }

    public async Task StopAsync()
    {
        Task previous;
        lock (gate)
        {
            stop?.Cancel();
            previous = worker;
        }
        try { await previous.ConfigureAwait(false); }
        catch (Exception) { /* PlayAsync reports the performance error. Retry any outstanding releases. */ }
        lock (gate)
        {
            if (worker != previous || held.Count == 0) return;
            try { ReleaseAll(); }
            catch (Exception ex)
            {
                Publish("error", status.Position, status.Duration, ex.Message);
                throw;
            }
            Publish("stopped", status.Position, status.Duration, "已停止，按键已释放");
        }
    }

    public void Dispose()
    {
        lock (gate) disposed = true;
        StopAsync().GetAwaiter().GetResult();
        lock (gate)
        {
            stop?.Dispose();
            stop = null;
        }
    }
}
