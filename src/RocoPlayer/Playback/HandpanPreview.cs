using NAudio.Wave;
using RocoPlayer.Music;

namespace RocoPlayer.Playback;

// The recordings are fixed pitches. Rebinding a game key does not retune its WAV file.
internal sealed class HandpanPreview : ISampleProvider
{
    internal const int SampleRate = 24_000;
    private const int Channels = 2;
    private const int SecondsPerRecording = 6;
    private const int MaxVoices = 128;
    private static readonly int[] RecordedPitches = [45, 52, 53, 55, 57, 59, 60, 62, 64];
    private const string RecordedKeys = "BFGHJKTYU";
    private readonly record struct Tuning(int Sample, double Rate, double Seconds);
    private readonly record struct Strike(long Start, long End, Tuning Tuning);
    private static readonly Tuning[] Tunings = Enumerable.Range(0, 128).Select(Tune).ToArray();
    private static readonly Lazy<float[][]> Recordings = new(LoadRecordings);
    private readonly float[][] samples;
    private readonly Strike[] strikes;
    private readonly List<Strike> active = new(MaxVoices);
    private readonly HandpanCompressor compressor = new(SampleRate);
    private readonly CancellationToken token;
    private readonly long frames;
    private long position;
    private int next;

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);
    public double EndPosition { get; }
    public double TotalDuration { get; }

    private static Tuning Tune(int pitch)
    {
        // The web player keeps the lower recording when two keys are equally close.
        int sample = Enumerable.Range(0, RecordedPitches.Length).MinBy(i => Math.Abs(RecordedPitches[i] - pitch));
        double rate = Math.Pow(2, (pitch - RecordedPitches[sample]) / 12d);
        return new(sample, rate, Math.Min(8, SecondsPerRecording / rate));
    }

    private static float[][] LoadRecordings()
    {
        var bank = new float[RecordedKeys.Length][];
        for (int i = 0; i < bank.Length; i++)
        {
            string resource = $"RocoPlayer.Handpan.v2.{RecordedKeys[i]}.wav";
            using var stream = typeof(HandpanPreview).Assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidDataException($"缺少手碟录音 {RecordedKeys[i]}");
            using var wave = new WaveFileReader(stream);
            var format = wave.WaveFormat;
            if (format.Encoding != WaveFormatEncoding.Pcm || format.BitsPerSample != 16 || format.Channels != Channels ||
                format.SampleRate != SampleRate || wave.SampleCount != SampleRate * SecondsPerRecording)
                throw new InvalidDataException($"手碟录音 {RecordedKeys[i]} 的格式不正确");
            int length = checked((int)wave.SampleCount * Channels);
            var data = new float[length + Channels]; // A silent frame for interpolation at the end.
            var provider = wave.ToSampleProvider();
            int read = 0;
            while (read < length)
            {
                int count = provider.Read(data, read, length - read);
                if (count == 0) throw new EndOfStreamException($"手碟录音 {RecordedKeys[i]} 不完整");
                read += count;
            }
            bank[i] = data;
        }
        return bank;
    }

    internal static double Duration(Score score, double speed) =>
        score.Notes.Aggregate(score.Duration, (end, note) => Math.Max(end, note.Start + Tunings[note.Pitch].Seconds * speed));

    public HandpanPreview(Score score, double start, double? seconds, double speed, CancellationToken token = default)
    {
        if (!double.IsFinite(speed) || speed is < .25 or > 4) throw new ArgumentOutOfRangeException(nameof(speed));
        this.token = token;
        token.ThrowIfCancellationRequested();
        score.Validate();
        TotalDuration = Duration(score, speed);
        double length = AuditionPlayer.Clip(start / speed, seconds, TotalDuration / speed);
        EndPosition = Math.Min(TotalDuration, start + length * speed);
        frames = Math.Max(1, checked((long)Math.Round(length * SampleRate)));

        var scheduled = new List<Strike>();
        foreach (var note in score.Notes)
        {
            token.ThrowIfCancellationRequested();
            var tuning = Tunings[note.Pitch];
            long onset = checked((long)Math.Round((note.Start - start) / speed * SampleRate));
            long end = onset + (long)Math.Ceiling(tuning.Seconds * SampleRate);
            if (end > 0 && onset < frames) scheduled.Add(new(onset, end, tuning));
        }
        strikes = scheduled.OrderBy(s => s.Start).ToArray();
        samples = Recordings.Value;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        token.ThrowIfCancellationRequested();
        int frameCount = (int)Math.Min(count / Channels, frames - position);
        if (frameCount == 0) return 0;
        Array.Clear(buffer, offset, frameCount * Channels);
        long end = position + frameCount;
        active.RemoveAll(s => s.End <= position);
        foreach (var strike in active) Mix(strike, position, end, buffer, offset);
        while (next < strikes.Length && strikes[next].Start < end)
        {
            token.ThrowIfCancellationRequested();
            var strike = strikes[next++];
            long begin = Math.Max(position, strike.Start);
            if (active.Count == MaxVoices) active.RemoveAll(s => s.End <= begin);
            if (active.Count == MaxVoices) continue;
            Mix(strike, begin, end, buffer, offset);
            active.Add(strike);
        }
        compressor.Process(buffer.AsSpan(offset, frameCount * Channels));
        position = end;
        return frameCount * Channels;
    }

    private void Mix(Strike strike, long begin, long end, float[] output, int offset)
    {
        var audio = samples[strike.Tuning.Sample];
        double step = strike.Tuning.Rate;
        long age = begin - strike.Start;
        int index = offset + checked((int)(begin - position) * Channels);
        int remaining = (int)(Math.Min(strike.End, end) - begin);
        for (int i = 0; i < remaining; i++, index += Channels)
        {
            double phase = (age + i) * step;
            int frame = (int)phase;
            if (frame >= audio.Length / Channels - 1) break;
            float fraction = (float)(phase - frame);
            int j = frame * Channels;
            output[index] += audio[j] + fraction * (audio[j + 2] - audio[j]);
            output[index + 1] += audio[j + 1] + fraction * (audio[j + 3] - audio[j + 1]);
        }
    }
}
