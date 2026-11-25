using System.Numerics;
using RocoPlayer.Music;

namespace RocoPlayer.Audio;

internal static class PitchDetector
{
    internal const int Hop = 220;
    private const int Frame = 2048;
    private const int Rate = AudioTranscriber.Rate;

    // YIN cumulative mean normalized difference and parabolic interpolation at the first dip.
    private static (double Pitch, double Confidence) Estimate(ReadOnlySpan<float> frame)
    {
        const int maxLag = Rate / 65, minLag = Rate / 2100;
        Span<double> normalized = stackalloc double[maxLag + 1];
        double cumulative = 0;
        for (int lag = 1; lag <= maxLag; lag++)
        {
            var a = frame[..^lag];
            var b = frame[lag..];
            var sum = Vector<float>.Zero;
            int i = 0;
            for (; i <= a.Length - Vector<float>.Count; i += Vector<float>.Count)
            {
                var delta = new Vector<float>(a[i..]) - new Vector<float>(b[i..]);
                sum += delta * delta;
            }
            double diff = Vector.Sum(sum);
            for (; i < a.Length; i++) diff += (a[i] - b[i]) * (a[i] - b[i]);
            cumulative += diff;
            normalized[lag] = cumulative < 1e-12 ? 1 : diff * lag / cumulative;
            if (lag <= minLag || normalized[lag - 1] >= 0.15 || normalized[lag] < normalized[lag - 1]) continue;
            double left = normalized[lag - 2], center = normalized[lag - 1], right = normalized[lag];
            double curvature = left - 2 * center + right;
            var refined = lag - 1 + (Math.Abs(curvature) > 1e-12 ? 0.5 * (left - right) / curvature : 0);
            return (69 + 12 * Math.Log2(Rate / (refined * 440)), 1 - center);
        }
        return (0, 0);
    }

    private static double Rms(ReadOnlySpan<float> frame)
    {
        double sum = 0, squares = 0;
        foreach (var value in frame) { sum += value; squares += (double)value * value; }
        double mean = sum / frame.Length;
        return Math.Sqrt(Math.Max(0, squares / frame.Length - mean * mean));
    }

    internal static MeasuredNote Measure(float[] samples, string key)
    {
        List<(double Pitch, double Confidence)> measurements = [];
        for (int start = 0; start + Frame <= samples.Length; start += Hop)
        {
            var frame = samples.AsSpan(start, Frame);
            if (Rms(frame) < 0.0002) continue;
            var (pitch, confidence) = Estimate(frame);
            if (confidence >= 0.9) measurements.Add((pitch, confidence));
        }
        if (measurements.Count < 8) throw new InvalidDataException("未检测到稳定音高，请检查输出设备、游戏音效和背景音乐。");
        double median = Median(measurements.Select(m => m.Pitch));
        var stable = measurements.Where(m => Math.Abs(m.Pitch - median) < 0.35).ToArray();
        if (stable.Length < 0.7 * measurements.Count)
            throw new InvalidDataException("音高不稳定，请关闭背景音乐后重试。");
        int nearest = (int)Math.Round(median);
        return new MeasuredNote(key, 440 * Math.Pow(2, (median - 69) / 12), nearest,
            (median - nearest) * 100, Median(stable.Select(m => m.Confidence)));

        static double Median(IEnumerable<double> values)
        {
            var sorted = values.Order().ToArray();
            return (sorted[sorted.Length / 2] + sorted[(sorted.Length - 1) / 2]) / 2;
        }
    }

    internal static List<Note> SoloNotes(float[] data, CancellationToken token, IProgress<double>? progress)
    {
        int count = Math.Max(0, (data.Length - Frame) / Hop + 1);
        var energy = new double[count];
        for (int i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            energy[i] = Rms(data.AsSpan(i * Hop, Frame));
        }
        double threshold = Math.Max(0.001, energy.Max() * 0.08);
        var raw = new int[count];
        for (int i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            var (pitch, confidence) = energy[i] >= threshold ? Estimate(data.AsSpan(i * Hop, Frame)) : default;
            raw[i] = confidence >= 0.85 ? (int)Math.Round(pitch) : -1;
            if (i % 100 == 0) progress?.Report(0.1 + 0.85 * i / count);
        }
        Span<int> window = stackalloc int[5];
        var detected = new int[count];
        for (int i = 0; i < count; i++)
        {
            for (int j = 0; j < 5; j++) window[j] = raw[Math.Clamp(i + j - 2, 0, count - 1)];
            window.Sort();
            detected[i] = window[2];
        }
        List<Note> notes = [];
        int begin = 0;
        for (int end = 1; end <= count; end++)
        {
            if (end < count && detected[end] == detected[begin]) continue;
            var duration = (end - begin) * (double)Hop / Rate;
            if (detected[begin] >= 0 && duration >= 0.08)
                notes.Add(new Note(begin * (double)Hop / Rate, duration, detected[begin]));
            begin = end;
        }
        return notes;
    }
}
