using System.Numerics;

namespace RocoPlayer.Audio;

internal sealed class PitchEnvelope(float[] values)
{
    private const int Rate = AudioTranscriber.Rate, Stride = 22;

    // Zero-phase third-order Butterworth bandpass, +/-75 cents. For a narrow
    // passband, the centered derivative supplies the quadrature component of
    // the carrier. Smooth its magnitude over 8 ms and retain millisecond samples.
    internal static PitchEnvelope Extract(float[] data, int pitch, CancellationToken token)
    {
        double hz = 440 * Math.Pow(2, (pitch - 69) / 12.0);
        double lo = Math.Tan(Math.PI * hz * Math.Pow(2, -0.75 / 12) / Rate);
        double hi = Math.Tan(Math.PI * hz * Math.Pow(2, 0.75 / 12) / Rate);
        var buffer = (float[])data.Clone();
        for (int k = 0; k < 3; k++)
        {
            var prototype = Complex.FromPolarCoordinates(1, Math.PI * (2 * k + 4) / 6);
            var mid = prototype * (hi - lo) / 2;
            var delta = Complex.Sqrt(mid * mid - lo * hi);
            // Exactly one root of each conjugate pair is in the upper half-plane.
            var analog = (mid + delta).Imaginary > 0 ? mid + delta : mid - delta;
            var pole = (1 + analog) / (1 - analog);
            double a1 = -2 * pole.Real, a2 = pole.Magnitude * pole.Magnitude;
            var z = Complex.FromPolarCoordinates(1, -2 * Math.PI * hz / Rate);
            double gain = ((1 + a1 * z + a2 * z * z) / (1 - z * z)).Magnitude;
            Filter(0, buffer.Length, 1);
            Filter(buffer.Length - 1, -1, -1);

            void Filter(int start, int stop, int step)
            {
                double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
                for (int i = start; i != stop; i += step)
                {
                    if ((i & 16383) == 0) token.ThrowIfCancellationRequested();
                    double x = buffer[i], y = gain * (x - x2) - a1 * y1 - a2 * y2;
                    buffer[i] = (float)y;
                    x2 = x1; x1 = x; y2 = y1; y1 = y;
                }
            }
        }
        double quadratureScale = 0.5 / Math.Sin(2 * Math.PI * hz / Rate);
        double left = buffer[0];
        for (int i = 0; i < buffer.Length; i++)
        {
            double current = buffer[i], right = buffer[Math.Min(i + 1, buffer.Length - 1)];
            double quadrature = (right - left) * quadratureScale;
            buffer[i] = (float)Math.Sqrt(current * current + quadrature * quadrature);
            left = current;
        }
        const int radius = 88;
        var envelope = new float[(data.Length + Stride - 1) / Stride];
        for (int i = 0; i < envelope.Length; i++)
        {
            int center = i * Stride, begin = Math.Max(0, center - radius), end = Math.Min(data.Length, center + radius);
            double sum = 0;
            for (int j = begin; j < end; j++) sum += buffer[j];
            envelope[i] = (float)(sum / (end - begin));
        }
        return new(envelope);
    }

    internal float At(double seconds)
    {
        double index = Math.Clamp(seconds * Rate / Stride, 0, values.Length - 1);
        int left = (int)index;
        return (float)(values[left] + (values[Math.Min(left + 1, values.Length - 1)] - values[left]) * (index - left));
    }

    internal bool IsAttack(double seconds)
    {
        var before = Section(-0.13, -0.06);
        var after = Section(0.06, 0.13);
        Array.Sort(before); Array.Sort(after);
        double level = Math.Max(1e-12, Math.Max(before[before.Length / 2], after[after.Length / 2]));
        return Section(-0.05, 0.05).Min() < 0.65 * level &&
            Section(0, 0.08).Max() > 1.5 * Math.Max(1e-12, Section(-0.09, -0.01).Min());

        float[] Section(double begin, double end) =>
            values[(int)Math.Ceiling((seconds + begin) * Rate / Stride)..(int)Math.Ceiling((seconds + end) * Rate / Stride)];
    }
}
