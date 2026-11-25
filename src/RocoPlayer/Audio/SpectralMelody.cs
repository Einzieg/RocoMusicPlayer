using NAudio.Dsp;
using RocoPlayer.Music;

namespace RocoPlayer.Audio;

internal static class SpectralMelody
{
    private const int Rate = AudioTranscriber.Rate, Hop = PitchDetector.Hop;
    private const int Window = 4096, FftSize = 8192, Spectrum = FftSize / 2 + 1;
    private const int Lowest = 36, FirstLead = 48, Highest = 96, Width = Highest - FirstLead + 1;

    internal static List<Note> Transcribe(float[] data, CancellationToken token, IProgress<double>? progress)
    {
        var bins = new (int Lower, double Fraction)[Highest - Lowest + 1, 3, 6];
        var weights = Enumerable.Range(1, 6).Select(h => Math.Pow(h, -0.8)).ToArray();
        var totalWeight = weights.Sum();
        for (int p = Lowest; p <= Highest; p++)
            for (int detune = 0; detune < 3; detune++)
                for (int h = 1; h <= 6; h++)
                {
                    double f = 440 * Math.Pow(2, (p + 0.3 * (detune - 1) - 69) / 12.0);
                    double bin = f * h * FftSize / Rate;
                    bins[p - Lowest, detune, h - 1] = ((int)bin, bin % 1);
                }

        int count = (data.Length + Hop - 1) / Hop;
        var energy = new double[count];
        var active = new bool[count];
        var flux = new double[count];
        var salience = new float[count, Width];
        var amplitude = new float[count, Width];
        var fft = new Complex[FftSize];
        var hann = Enumerable.Range(0, Window).Select(i => 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (Window - 1))).ToArray();
        var magnitude = new double[Spectrum];
        var previous = new double[Spectrum];
        var prefix = new double[Spectrum + 1];
        var whitened = new double[Spectrum];
        var evidence = new double[Highest - Lowest + 1];
        double maxEnergy = 0;
        for (int frame = 0; frame < count; frame++)
        {
            token.ThrowIfCancellationRequested();
            int begin = frame * Hop - Window / 2;
            int left = Math.Max(0, -begin), right = Math.Min(Window, data.Length - begin);
            double mean = 0;
            for (int i = left; i < right; i++) mean += data[begin + i];
            mean /= Window;
            Array.Clear(fft);
            double rms = 0;
            for (int i = 0; i < Window; i++)
            {
                double v = (i >= left && i < right ? data[begin + i] : 0) - mean;
                fft[i].X = (float)(v * hann[i]);
                if (i >= Window / 2 - 512 && i < Window / 2 + 512) rms += v * v;
            }
            energy[frame] = Math.Sqrt(rms / 1024);
            maxEnergy = Math.Max(maxEnergy, energy[frame]);
            FastFourierTransform.FFT(true, 13, fft);
            double peak = 0, power = 0, logPower = 0, instantaneousFlux = 0, bandMass = 0;
            int bandStart = (int)Math.Round(130.0 * FftSize / Rate);
            int bandEnd = (int)Math.Round(5000.0 * FftSize / Rate);
            for (int i = 0; i < Spectrum; i++)
            {
                var v = fft[i];
                magnitude[i] = FftSize * Math.Sqrt(v.X * v.X + v.Y * v.Y);
                peak = Math.Max(peak, magnitude[i]);
                prefix[i + 1] = prefix[i] + magnitude[i];
                if (i < bandStart || i >= bandEnd) continue;
                var p = magnitude[i] * magnitude[i] + 1e-15;
                power += p;
                logPower += Math.Log(p);
                instantaneousFlux += Math.Max(0, magnitude[i] - previous[i]);
                bandMass += magnitude[i];
            }
            int bandwidth = bandEnd - bandStart;
            active[frame] = Math.Exp(logPower / bandwidth) < 0.3 * power / bandwidth;
            flux[frame] = instantaneousFlux / Math.Max(1e-10, bandMass);
            for (int i = 0; i < Spectrum; i++)
            {
                int l = Math.Max(0, i - 37), r = Math.Min(Spectrum, i + 38);
                double envelope = (prefix[r] - prefix[l]) / (r - l);
                whitened[i] = Math.Max(0, magnitude[i] - envelope) / Math.Pow(Math.Max(Math.Max(1e-10, peak * 0.015), envelope), 0.7);
            }
            for (int p = Lowest; p <= Highest; p++)
            {
                double best = -1, amplitudeAtBest = 0;
                for (int t = 0; t < 3; t++)
                {
                    double score = 0, strongest = 0, fundamental = 0, raw = 0;
                    for (int h = 0; h < 6; h++)
                    {
                        var (l, f) = bins[p - Lowest, t, h];
                        if (l >= Spectrum - 2) continue;
                        double sal = whitened[l] + f * (whitened[l + 1] - whitened[l]);
                        if (h == 0) fundamental = sal;
                        strongest = Math.Max(strongest, sal);
                        score += weights[h] * sal;
                        if (h < 2) raw += (magnitude[l] + f * (magnitude[l + 1] - magnitude[l])) / (h + 1);
                    }
                    score = score / totalWeight * Math.Sqrt(Math.Min(1, fundamental / (0.2 * strongest + 1e-10)));
                    if (score > best) { best = score; amplitudeAtBest = raw; }
                }
                evidence[p - Lowest] = best;
                if (p >= FirstLead) amplitude[frame, p - FirstLead] = (float)amplitudeAtBest;
            }
            double peakLead = 0;
            for (int p = FirstLead; p <= Highest; p++)
            {
                int j = p - Lowest;
                double explained = 0.65 * evidence[j - 12];
                if (j >= 19) explained = Math.Max(explained, 0.45 * evidence[j - 19]);
                if (j >= 24) explained = Math.Max(explained, 0.3 * evidence[j - 24]);
                if (j >= 28) explained = Math.Max(explained, 0.2 * evidence[j - 28]);
                var value = Math.Max(0, evidence[j] - explained) * Math.Clamp((p - 45) / 15.0, 0.15, 1);
                peakLead = Math.Max(peakLead, value);
                salience[frame, p - FirstLead] = (float)value;
            }
            active[frame] &= peakLead > evidence.Max() * 0.12;
            (magnitude, previous) = (previous, magnitude);
            if (frame % 100 == 0) progress?.Report(0.1 + 0.65 * frame / count);
        }

        var emitted = new byte[count];
        var back = new byte[count, Width + 1];
        var scores = new double[Width + 1];
        var next = new double[Width + 1];
        var costs = new double[Width + 1, Width + 1];
        for (int s = 0; s <= Width; s++)
            for (int t = 0; t <= Width; t++)
            {
                int d = Math.Abs(s - t);
                costs[s, t] = d == 0 ? 0 : s == Width || t == Width ? 1.5 : 1.4 + Math.Min(3, 0.18 * d) + (d == 12 ? 0.6 : 0);
            }
        for (int frame = 0; frame < count; frame++)
        {
            token.ThrowIfCancellationRequested();
            bool audible = active[frame] && energy[frame] >= Math.Max(0.0003, maxEnergy * 0.015);
            double peak = 1e-10;
            for (int t = 0; t < Width; t++) peak = Math.Max(peak, salience[frame, t]);
            for (int t = 0; t <= Width; t++)
            {
                double emission = t == Width ? (audible ? -2.5 : 0)
                    : audible ? 2 * Math.Log(Math.Max(0.001, salience[frame, t] / peak)) : -20;
                if (frame == 0) { next[t] = emission; continue; }
                double best = double.NegativeInfinity, scale = flux[frame] > 0.15 ? 0.55 : 1;
                for (int s = 0; s <= Width; s++)
                {
                    double v = scores[s] - scale * costs[s, t];
                    if (v > best) { best = v; back[frame, t] = (byte)s; }
                }
                next[t] = best + emission;
            }
            (scores, next) = (next, scores);
            if (frame % 100 == 0) progress?.Report(0.75 + 0.2 * frame / count);
        }
        emitted[^1] = (byte)Array.IndexOf(scores, scores.Max());
        for (int i = count - 1; i > 0; i--) emitted[i - 1] = back[i, emitted[i]];
        var notes = new List<Note>();
        int from = 0;
        for (int end = 1; end <= count; end++)
        {
            if (end < count && emitted[end] == emitted[from]) continue;
            int p = emitted[from];
            if (p != Width)
            {
                var amp = new float[end - from];
                for (int i = from; i < end; i++) amp[i - from] = amplitude[i, p];
                int start = from;
                // A repeated same-pitch attack must emerge from a valley in that pitch's own envelope.
                float max = amp.Max(), prevPeak = 0;
                for (int i = 3; i < amp.Length - 1; i++)
                {
                    prevPeak = Math.Max(prevPeak, amp[i - 3]);
                    double rise = amp[i] - amp[i - 3];
                    if (rise <= max * 0.12 || rise < amp[i + 1] - amp[i - 2] || rise <= amp[i - 1] - amp[Math.Max(0, i - 4)]) continue;
                    var preceding = amp.AsSpan(Math.Max(0, i - 10), Math.Min(i, 10));
                    float valley = float.MaxValue;
                    foreach (float v in preceding) valley = Math.Min(v, valley);
                    float after = 0;
                    for (int k = i; k < Math.Min(i + 6, amp.Length); k++) after = Math.Max(after, amp[k]);
                    int onset = from + i;
                    if (valley >= 0.75 * Math.Min(prevPeak, after) || onset - start < 9 || end - onset < 9) continue;
                    Add(start, onset, p);
                    start = onset;
                }
                Add(start, end, p);
            }
            from = end;
        }
        return notes;

        void Add(int begin, int finish, int p)
        {
            double start = begin * (double)Hop / Rate;
            double duration = Math.Min(finish * (double)Hop / Rate, (double)data.Length / Rate) - start;
            if (duration >= 0.08) notes.Add(new(start, duration, p + FirstLead));
        }
    }
}
