using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using RocoPlayer.Music;

namespace RocoPlayer.Audio;

public sealed record MeasuredNote(string Key, double Frequency, int Pitch, double Cents, double Confidence);

public static class AudioTranscriber
{
    public const string Revision = "csharp-v1";
    internal const int Rate = 22050;

    public static WaveStream OpenAudio(string path) =>
        Path.GetExtension(path).Equals(".ogg", StringComparison.OrdinalIgnoreCase)
            ? new VorbisWaveReader(path) : new AudioFileReader(path);

    public static Score Transcribe(string path, string engine = "instrument", string register = "auto",
        string texture = "melody", CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        if (engine is not ("instrument" or "melody" or "yin") ||
            register is not ("auto" or "high" or "mid" or "low"))
            throw new ArgumentException("未知转谱引擎或音区。");
        if (texture != "melody" && (texture != "chords" || engine != "instrument"))
            throw new ArgumentException("保留和弦模式仅支持 Basic Pitch。");

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(0);
        using var source = OpenAudio(path);
        if (source.TotalTime.TotalSeconds is < 0.1 or > 300 ||
            source.WaveFormat.SampleRate is < 8000 or > 192000 || source.WaveFormat.Channels is < 1 or > 8)
            throw new InvalidDataException("请选用 0.1～300 秒、8～192 kHz、最多 8 声道的音频。");

        var provider = new MonoSource(source.ToSampleProvider(), engine == "instrument");
        var data = Resample(provider, cancellationToken, n => progress?.Report(0.07 *
            Math.Min(1, n / (Rate * source.TotalTime.TotalSeconds))));
        var notes = engine switch
        {
            "instrument" => BasicPitch.Transcribe(data, register, texture == "chords", cancellationToken, progress),
            "yin" => PitchDetector.SoloNotes(data, cancellationToken, progress),
            _ => SpectralMelody.Transcribe(data, cancellationToken, progress)
        };
        if (notes.Count == 0) throw new InvalidDataException("未识别到稳定音符，请尝试旋律更清晰的片段。");
        var tracks = new Dictionary<int, string> { [0] = engine == "instrument" ? "Basic Pitch instrument lead" : "音频识别 · 待校对" };
        if (texture == "chords") tracks[1] = "Basic Pitch supporting chord notes";
        progress?.Report(1);
        cancellationToken.ThrowIfCancellationRequested();
        return new Score(Path.GetFileNameWithoutExtension(path), notes, (double)data.Length / Rate, tracks);
    }

    public static int MeasurePitch(float[] mono, int sampleRate) => MeasureNote(mono, sampleRate, "").Pitch;

    public static MeasuredNote MeasureNote(float[] mono, int sampleRate, string key)
    {
        if (sampleRate is < 8000 or > 192000 || mono.Any(v => !float.IsFinite(v)))
            throw new InvalidDataException("校准录音的采样率或数据无效。");
        var samples = sampleRate == Rate ? mono : Resample(new ArraySource(mono, sampleRate), default, null);
        return PitchDetector.Measure(samples, key);
    }

    private static float[] Resample(ISampleProvider source, CancellationToken token, Action<int>? progress)
    {
        if (source.WaveFormat.SampleRate != Rate) source = new WdlResamplingSampleProvider(source, Rate);
        var samples = new List<float>();
        var buffer = new float[8192];
        int count;
        while ((count = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();
            if (samples.Count + count > Rate * 300 + 1)
                throw new InvalidDataException("音频超过五分钟，请先截取片段。");
            samples.AddRange(buffer.AsSpan(0, count));
            progress?.Invoke(samples.Count);
        }
        if (samples.Count < Rate / 10) throw new InvalidDataException("音频太短，无法识别。");
        return samples.ToArray();
    }

    private sealed class ArraySource(float[] samples, int sampleRate) : ISampleProvider
    {
        private int position;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
        public int Read(float[] buffer, int offset, int count)
        {
            count = Math.Min(count, samples.Length - position);
            Array.Copy(samples, position, buffer, offset, count);
            position += count;
            return count;
        }
    }

    // Choose the most energetic channel when averaging would cancel an out-of-phase recording.
    private sealed class MonoSource(ISampleProvider source, bool mix) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        private float[] interleaved = [];
        public int Read(float[] buffer, int offset, int count)
        {
            int channels = source.WaveFormat.Channels;
            if (interleaved.Length < count * channels) interleaved = new float[count * channels];
            int frames = source.Read(interleaved, 0, count * channels) / channels;
            Span<double> energy = stackalloc double[8];
            energy.Clear();
            double averageEnergy = 0;
            for (int i = 0; i < frames; i++)
            {
                float sum = 0;
                for (int ch = 0; ch < channels; ch++)
                {
                    var value = interleaved[i * channels + ch];
                    if (!float.IsFinite(value)) throw new InvalidDataException("音频含有非有限采样值。");
                    sum += value;
                    energy[ch] += (double)value * value;
                }
                float average = sum / channels;
                buffer[offset + i] = average;
                averageEnergy += (double)average * average;
            }
            int loudest = 0;
            for (int ch = 1; ch < channels; ch++)
                if (energy[ch] > energy[loudest]) loudest = ch;
            if (!mix || averageEnergy < energy[loudest] * 0.01)
                for (int i = 0; i < frames; i++) buffer[offset + i] = interleaved[i * channels + loudest];
            return frames;
        }
    }
}
