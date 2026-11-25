using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using RocoPlayer.Audio;
using RocoPlayer.Music;

namespace RocoPlayer.Playback;

public sealed class AuditionPlayer : IDisposable
{
    private readonly object gate = new();
    private Task playback = Task.CompletedTask;
    private CancellationTokenSource? cancel;
    private bool disposed;
    private sealed record OutputSession(WaveOutEvent Device, int BytesPerSecond, double Start, double End, double Total, double Speed);
    private OutputSession? current;

    internal (double Position, double Total)? Progress
    {
        get
        {
            lock (gate)
            {
                if (current is not { } session) return null;
                try
                {
                    double played = session.Device.GetPosition() / (double)session.BytesPerSecond;
                    return (Math.Min(session.End, session.Start + played * session.Speed), session.Total);
                }
                catch (NAudio.MmException) { return null; } // PlaybackStopped reports device errors to the caller.
            }
        }
    }

    public Task PlayScoreAsync(Score score, double start, double duration, CancellationToken cancellationToken = default)
        => PlayScoreAsync(score, start, duration, 1, cancellationToken);

    internal Task PlayScoreAsync(Score score, double start, double? duration, double speed, CancellationToken cancellationToken = default) =>
        Start(token =>
        {
            var preview = new HandpanPreview(score, start, duration, speed, token);
            Play(preview, start, preview.EndPosition, preview.TotalDuration, speed, token);
        }, cancellationToken);

    public Task PlaySourceAsync(string path, double start, double duration, CancellationToken cancellationToken = default)
        => PlaySourceAsync(path, start, (double?)duration, cancellationToken);

    internal Task PlaySourceAsync(string path, double start, double? duration, CancellationToken cancellationToken = default) =>
        Start(token =>
        {
            if (!File.Exists(path) || Path.GetExtension(path).ToLowerInvariant() is not (".mp3" or ".wav" or ".flac" or ".ogg"))
                throw new InvalidOperationException("原音频不存在，或此乐谱来自 TXT/MIDI");
            using var wave = AudioTranscriber.OpenAudio(path);
            double total = wave.TotalTime.TotalSeconds;
            double length = Clip(start, duration, total);
            if (wave.WaveFormat.SampleRate is < 8000 or > 384000 || wave.WaveFormat.Channels is < 1 or > 8)
                throw new InvalidDataException("原音频的采样率或声道数不支持试听");
            wave.CurrentTime = TimeSpan.FromSeconds(start);
            ISampleProvider source = wave.ToSampleProvider();
            if (source.WaveFormat.Channels > 2) source = new MultiplexingSampleProvider([source], 2);
            if (source.WaveFormat.SampleRate != 22050) source = new WdlResamplingSampleProvider(source, 22050);
            var clip = new OffsetSampleProvider(source) { Take = TimeSpan.FromSeconds(length) };
            Play(clip, start, start + length, total, 1, token);
        }, cancellationToken);

    internal static double Clip(double start, double? duration, double total)
    {
        if (!double.IsFinite(total) || !double.IsFinite(start) || start < 0 || start >= total)
            throw new ArgumentException("试听起点必须在曲目范围内");
        if (duration is not double length) return total - start;
        if (!double.IsFinite(length) || length is < .1 or > 30) throw new ArgumentException("试听长度必须在 0.1～30 秒之间");
        return Math.Min(length, total - start);
    }

    private void Play(ISampleProvider source, double start, double end, double total, double speed, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var output = new WaveOutEvent { DesiredLatency = 100 };
        output.PlaybackStopped += (_, args) =>
        {
            if (args.Exception != null && !token.IsCancellationRequested)
                stopped.TrySetException(new InvalidOperationException("音频输出失败", args.Exception));
            else stopped.TrySetResult();
        };
        var pcm = source.ToWaveProvider16();
        output.Init(pcm);
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            output.Play();
            current = new(output, pcm.WaveFormat.AverageBytesPerSecond, start, end, total, speed);
        }
        try { stopped.Task.WaitAsync(token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            output.Stop();
            stopped.Task.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        }
        finally { lock (gate) current = null; }
    }

    private Task Start(Action<CancellationToken> play, CancellationToken caller)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancel?.Cancel();
            current = null;
            var prior = playback;
            var old = cancel;
            cancel = CancellationTokenSource.CreateLinkedTokenSource(caller);
            var token = cancel.Token;
            playback = Task.Run(async () =>
            {
                try { await prior.ConfigureAwait(false); }
                catch (Exception) { /* The previous caller receives its playback error. */ }
                old?.Dispose();
                try
                {
                    token.ThrowIfCancellationRequested();
                    play(token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            });
            return playback;
        }
    }

    public void Stop() { lock (gate) cancel?.Cancel(); }
    public void Dispose()
    {
        lock (gate) { disposed = true; cancel?.Cancel(); }
        try { playback.GetAwaiter().GetResult(); }
        catch (Exception) { /* Reported to the caller. */ }
        lock (gate) { cancel?.Dispose(); cancel = null; }
    }
}
