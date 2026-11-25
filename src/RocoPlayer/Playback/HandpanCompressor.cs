namespace RocoPlayer.Playback;

// Stereo-linked dynamics for RocoMusic's Web Audio output chain.
// The algorithm and corresponding licenses are documented in THIRD_PARTY_NOTICES.md.
internal sealed class HandpanCompressor
{
    private const double Volume = .8;
    private const double Ratio = 12;
    private static readonly double Threshold = Math.Pow(10, -6d / 20);
    // A 6 dB knee above -6 dB ends at 0 dB (linear amplitude 1).
    private static readonly double Knee = KneeCoefficient();
    private static readonly double KneeEnd = SoftKnee(1, Knee);
    private static readonly double Makeup = Math.Pow(KneeEnd, -.6);

    private readonly float[] delayed;
    private readonly double attackFrames, releaseFrames, detectorFrames;
    private int delayIndex, groupFrame;
    private double detector, target, rate, gain = 1, maxAttackDb = -1;

    public HandpanCompressor(int sampleRate)
    {
        delayed = new float[(int)(sampleRate * .006) * 2];
        attackFrames = sampleRate * .003;
        releaseFrames = sampleRate * .18;
        detectorFrames = sampleRate * .0025;
    }

    private static double SoftKnee(double level, double curvature) =>
        Threshold + (1 - Math.Exp(-curvature * (level - Threshold))) / curvature;

    private static double KneeCoefficient()
    {
        double low = .1, high = 10000, k = 5;
        for (int i = 0; i < 15; i++)
        {
            double slope = Math.Log(SoftKnee(1.001, k) / SoftKnee(1, k)) / Math.Log(1.001);
            if (slope < 1 / Ratio) high = k;
            else low = k;
            k = Math.Sqrt(low * high);
        }
        return k;
    }

    private void UpdateEnvelope()
    {
        target = Math.Asin(detector) * 2 / Math.PI;
        double difference = target > 0 ? 20 * Math.Log10(gain / target) : 1;
        if (gain < target)
        {
            maxAttackDb = -1;
            double x = (Math.Clamp(difference, -12, 0) + 12) / 4;
            // Web Audio's four adaptive release zones: .09, .16, .42, .98.
            double zone = .09 + x * (.00598875 + x * (.05040951 + x * (.01265542 + x * .00094632)));
            rate = Math.Pow(10, 5 / (20 * releaseFrames * zone));
        }
        else
        {
            maxAttackDb = Math.Max(maxAttackDb, difference);
            rate = 1 - Math.Pow(.25 / Math.Max(.5, maxAttackDb), 1 / attackFrames);
        }
    }

    public void Process(Span<float> stereo)
    {
        for (int i = 0; i < stereo.Length; i += 2)
        {
            if (groupFrame == 0) UpdateEnvelope();
            groupFrame = (groupFrame + 1) % 32;

            float left = (float)(stereo[i] * Volume), right = (float)(stereo[i + 1] * Volume);
            double level = Math.Max(Math.Abs(left), Math.Abs(right));
            double attenuation = 1;
            if (level > Threshold)
                attenuation = (level < 1 ? SoftKnee(level, Knee) : KneeEnd * Math.Pow(level, 1 / Ratio)) / level;
            double recoveryDb = Math.Max(2, -20 * Math.Log10(attenuation));
            detector = attenuation <= detector ? attenuation :
                Math.Min(1, detector + (attenuation - detector) * (Math.Pow(10, recoveryDb / (20 * detectorFrames)) - 1));

            gain = rate < 1 ? gain + (target - gain) * rate : Math.Min(1, gain * rate);
            float scale = (float)(Makeup * Math.Sin(Math.PI / 2 * gain));
            stereo[i] = delayed[delayIndex] * scale;
            stereo[i + 1] = delayed[delayIndex + 1] * scale;
            delayed[delayIndex] = left;
            delayed[delayIndex + 1] = right;
            delayIndex = (delayIndex + 2) % delayed.Length;
        }
    }
}
