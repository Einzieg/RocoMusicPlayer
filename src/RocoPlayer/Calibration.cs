using System.Diagnostics;
using System.Text.Json;
using NAudio.Wave;
using RocoPlayer.Audio;
using RocoPlayer.Music;
using RocoPlayer.Playback;

namespace RocoPlayer;

internal static class Calibration
{
    internal static async Task<List<Binding>> MeasureAsync(PlayerSettings settings, PlaybackEngine player,
        TargetWindow target, string folder, IProgress<string> progress, CancellationToken token)
    {
        var measurements = new List<MeasuredNote>();
        Directory.CreateDirectory(folder);
        foreach (var binding in settings.Mapping)
        {
            progress.Report($"正在校准 {measurements.Count + 1} / 9 · {binding.Key}");
            await WaitInForeground(800, target, token);
            using var capture = new WasapiLoopbackCapture();
            using var recording = new MemoryStream();
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            capture.DataAvailable += (_, e) => recording.Write(e.Buffer, 0, e.BytesRecorded);
            capture.RecordingStopped += (_, e) =>
            {
                if (e.Exception != null) stopped.TrySetException(e.Exception);
                else stopped.TrySetResult();
            };
            capture.StartRecording();
            try
            {
                await player.TapAsync(binding.Key, settings.Backend, target, 60, token);
                await WaitInForeground(800, target, token);
            }
            finally
            {
                capture.StopRecording();
                await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3));
            }
            token.ThrowIfCancellationRequested();
            recording.Position = 0;
            using var wave = new RawSourceWaveStream(recording, capture.WaveFormat);
            WaveFileWriter.CreateWaveFile(Path.Combine(folder, binding.Key + ".wav"), wave);
            wave.Position = 0;
            var provider = wave.ToSampleProvider();
            var samples = new float[checked((int)(recording.Length / capture.WaveFormat.BlockAlign) * capture.WaveFormat.Channels)];
            int count = provider.Read(samples, 0, samples.Length);
            float[] mono = LoudestChannel(samples, count, capture.WaveFormat.Channels);
            var note = AudioTranscriber.MeasureNote(mono, capture.WaveFormat.SampleRate, binding.Key);
            measurements.Add(note);
            progress.Report($"{binding.Key} = {note.Frequency:F1} Hz · MIDI {note.Pitch} ({note.Cents:+0.0;-0.0;0} 音分)");
        }
        if (measurements.Select(b => b.Pitch).Distinct().Count() != measurements.Count) throw new InvalidDataException("检测到重复音高，请关闭其他声音后重新校准");
        await File.WriteAllTextAsync(Path.Combine(folder, "measurements.json"), JsonSerializer.Serialize(measurements, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true }), token);
        return measurements.Select(n => new Binding(n.Pitch, n.Key)).ToList();
    }
    private static float[] LoudestChannel(float[] data, int count, int channels)
    {
        var energy = new double[channels];
        for (int i = 0; i < count; i++) energy[i % channels] += data[i] * data[i];
        int channel = Array.IndexOf(energy, energy.Max());
        return Enumerable.Range(0, count / channels).Select(i => data[i * channels + channel]).ToArray();
    }
    private static async Task WaitInForeground(int ms, TargetWindow target, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        do
        {
            token.ThrowIfCancellationRequested();
            if (target.State != "ready") throw new InvalidOperationException("游戏不在前台，校准已中止");
            await Task.Delay(25, token);
        } while (watch.ElapsedMilliseconds < ms);
    }
}
