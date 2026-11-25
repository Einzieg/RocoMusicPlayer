using NAudio.Wave;
using RocoPlayer.Audio;

const int rate = 44100;
var path = Path.Combine(Path.GetTempPath(), $"RocoPlayer-audio-{Guid.NewGuid():N}.wav");
try
{
    int[] expected = [69, 72, 74, 71, 69];
    var samples = new float[(int)((0.6 + 0.55 * expected.Length) * rate)];
    for (int j = 0; j < expected.Length; j++) AddTone(samples, 0.2 + j * 0.55, 0.44, expected[j]);
    WriteWave(samples);
    foreach (var engine in new[] { "instrument", "melody", "yin" })
    {
        var score = AudioTranscriber.Transcribe(path, engine);
        Console.WriteLine($"{engine} -> {string.Join(", ", score.Notes.Select(n => n.Pitch))}");
        Assert(score.Notes.Select(n => n.Pitch).SequenceEqual(expected), $"{engine} 音高顺序");
        Assert(Math.Abs(score.Duration - samples.Length / (double)rate) < 0.005, $"{engine} 静音尾段时长");
        Assert(Math.Abs(score.Notes[0].Start - 0.2) < 0.12, $"{engine} 起音时间");
    }
    var chord = new float[rate * 2];
    foreach (int pitch in new[] { 60, 64, 67 }) AddTone(chord, 0.3, 1.2, pitch);
    WriteWave(chord);
    var polyphony = AudioTranscriber.Transcribe(path, texture: "chords");
    Assert(new HashSet<int>(polyphony.Notes.Where(n => n.Start <= 0.6 && n.Start + n.Duration >= 0.6)
        .Select(n => n.Pitch)).IsSupersetOf([60, 64, 67]), "Basic Pitch 和弦模式");
    int[] lead = [64, 67, 69, 72, 71, 67, 64, 62];
    int[] bass = [40, 43, 45, 41];
    var ensemble = new float[(int)(rate * 5.3)];
    for (int j = 0; j < lead.Length; j++)
    {
        double start = 0.2 + j * 0.6;
        AddTone(ensemble, start, 0.5, lead[j], 0.13);
        if (j % 2 == 0) AddTone(ensemble, start, 1.1, bass[j / 2], 0.32);
    }
    WriteWave(ensemble);
    Assert(AudioTranscriber.Transcribe(path, register: "high").Notes.Select(n => n.Pitch).SequenceEqual(lead),
        "较强低音伴奏下的高音区旋律");

    var vibrato = new float[(int)(4.5 * rate)];
    double phase = 0;
    for (int i = 0; i < vibrato.Length; i++)
    {
        double age = (double)i / rate - 0.25;
        phase += 2 * Math.PI * 880 / rate * Math.Pow(2, 0.5 * Math.Sin(10 * Math.PI * age) / 12);
        double env = Math.Clamp(age / 0.015, 0, 1) * Math.Clamp((4 - age) / 0.035, 0, 1);
        vibrato[i] = (float)(0.16 * env * (Math.Sin(phase) + 0.4 * Math.Sin(2 * phase) + 0.2 * Math.Sin(3 * phase)));
    }
    WriteWave(vibrato);
    var sustained = AudioTranscriber.Transcribe(path).Notes.Single();
    Assert(sustained.Pitch == 81 && sustained.Duration > 3.8, "持续颤音不拆音");
    var calibration = new float[rate];
    AddTone(calibration, 0.1, 0.8, 69);
    var measured = AudioTranscriber.MeasureNote(calibration, rate, "J");
    Assert(measured.Pitch == 69 && Math.Abs(measured.Frequency - 440) < 1 &&
        Math.Abs(measured.Cents) < 4 && measured.Confidence >= 0.95, "A4 校准");

    using var cancel = new CancellationTokenSource();
    cancel.Cancel();
    try { AudioTranscriber.Transcribe(path, cancellationToken: cancel.Token); throw new Exception("取消无效"); }
    catch (OperationCanceledException) { }

    WriteWave(new float[rate * 2]);
    foreach (var engine in new[] { "instrument", "melody", "yin" })
    {
        try { AudioTranscriber.Transcribe(path, engine); throw new Exception($"{engine} 未拒绝静音"); }
        catch (InvalidDataException) { }
    }
    Console.WriteLine("Audio checks passed.");
}
finally
{
    File.Delete(path);
}

void WriteWave(float[] mono)
{
    using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(rate, 2));
    // Deliberately phase-invert the right channel to test the mono fallback.
    foreach (float value in mono) { writer.WriteSample(value); writer.WriteSample(-value); }
}

static void AddTone(float[] signal, double start, double duration, int pitch, double amplitude = 0.15)
{
    double hz = 440 * Math.Pow(2, (pitch - 69) / 12.0);
    int begin = (int)Math.Round(start * rate);
    for (int i = 0; i < (int)Math.Round(duration * rate); i++)
    {
        double t = (double)i / rate;
        double phase = 2 * Math.PI * hz * t;
        var envelope = Math.Clamp(t / 0.01, 0, 1) * Math.Clamp((duration - t) / 0.02, 0, 1);
        signal[begin + i] += (float)(amplitude * envelope *
            (Math.Sin(phase) + 0.45 * Math.Sin(2 * phase) + 0.2 * Math.Sin(3 * phase)));
    }
}

static void Assert(bool condition, string name)
{
    if (!condition) throw new Exception($"验证失败，{name}");
}
