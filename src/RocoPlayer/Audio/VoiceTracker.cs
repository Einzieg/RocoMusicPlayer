using RocoPlayer.Music;
using static RocoPlayer.Audio.BasicPitch;

namespace RocoPlayer.Audio;

internal static class VoiceTracker
{
    private static bool Trusted(Candidate n) => n.Confidence >= 0.45 && n.Support >= 0.65;

    internal static List<Note> Select(List<Candidate> candidates, List<Candidate> raw, int frames,
        float[] activations, Dictionary<int, float[]> envelopes, string register, CancellationToken token, IProgress<double>? progress)
    {
        var notes = candidates.Where(Trusted).ToArray();
        if (notes.Length == 0) return [];
        var times = Enumerable.Range(0, frames + 1).Select(Time).ToArray();
        var centers = RegisterCurve(raw.Where(Trusted), times[^1], register, token);
        // Prefix integrals allow a transition at the next onset to truncate only the
        // discarded tail of the preceding note. A sustained note cannot be entered midway.
        var integrals = new double[notes.Length][];
        for (int i = 0; i < notes.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var n = notes[i];
            var env = envelopes[n.Pitch].AsSpan(n.Begin, n.End - n.Begin).ToArray();
            Array.Sort(env);
            double quantileIndex = 0.85 * (env.Length - 1);
            int q = (int)quantileIndex;
            double scale = Math.Max(1e-9, env[q] + (quantileIndex - q) * (env[Math.Min(q + 1, env.Length - 1)] - env[q]));
            var prefix = integrals[i] = new double[n.End - n.Begin + 1];
            for (int f = n.Begin; f < n.End; f++)
            {
                double block = Math.Clamp((times[f] - 3) / 6, 0, centers.Length - 1);
                int b = (int)block;
                double center = centers[b] + (centers[Math.Min(b + 1, centers.Length - 1)] - centers[b]) * (block - b);
                double affinity = 0.25 + 0.75 * Math.Exp(-0.5 * Math.Pow((n.Pitch - center) / 12, 2));
                double evidence = activations[f * 88 + n.Pitch - 21] * Math.Min(1, envelopes[n.Pitch][f] / (0.5 * scale));
                var density = 3 * (evidence * affinity - 0.4) + 0.18 * (n.Onset - 0.5);
                prefix[f - n.Begin + 1] = prefix[f - n.Begin] + density * (times[f + 1] - times[f]);
            }
        }

        var best = new double[notes.Length];
        var previous = new int[notes.Length];
        int winner = 0;
        for (int i = 0; i < notes.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var n = notes[i];
            double score = 0;
            previous[i] = -1;
            for (int j = 0; j < i; j++)
            {
                var pred = notes[j];
                var cut = Math.Min(pred.End, n.Begin);
                if (times[cut] - times[pred.Begin] < 0.075) continue;
                double lost = integrals[j][^1] - integrals[j][cut - pred.Begin];
                double gap = Math.Max(0, times[n.Begin] - times[pred.End]);
                double jump = Math.Min(24, Math.Abs(n.Pitch - pred.Pitch));
                double penalty = 0.025 * jump * (1 - 0.55 * n.Onset) / (1 + gap) + (gap > 0.2 ? 0.06 : 0);
                double pathScore = best[j] - lost - penalty;
                if (pathScore > score) { score = pathScore; previous[i] = j; }
            }
            best[i] = score + integrals[i][^1] - 0.10;
            if (best[i] > best[winner]) winner = i;
            if (i % 64 == 0) progress?.Report(0.94 + 0.055 * (i + 1) / notes.Length);
        }
        if (best[winner] <= 0) return [];
        var path = new Stack<Candidate>();
        for (int p = winner; p >= 0; p = previous[p]) path.Push(notes[p]);
        var continuation = Enumerable.Range(0, notes.Length).ToDictionary(i => (notes[i].Pitch, notes[i].Begin));
        var result = new List<Note>();
        while (path.TryPop(out var n))
        {
            int next = path.TryPeek(out var nextNote) ? nextNote.Begin : frames;
            int end = Math.Min(n.End, next);
            result.Add(new(times[n.Begin], times[end] - times[n.Begin], n.Pitch));
            if (next == frames || times[next] - times[end] < 0.075 ||
                !continuation.TryGetValue((n.Pitch, end), out int j)) continue;
            if (notes[j].End >= next && integrals[j][next - end] > 0)
                result.Add(new(times[end], times[next] - times[end], n.Pitch));
        }
        return result;
    }

    private static int[] RegisterCurve(IEnumerable<Candidate> notes, double duration, string register, CancellationToken token)
    {
        int blocks = Math.Max(1, (int)Math.Ceiling(duration / 6));
        var bins = new double[blocks, 88];
        foreach (var n in notes)
        {
            var length = Time(n.End) - Time(n.Begin);
            double weight = Math.Min(length, 0.7) * Math.Pow(Math.Min(1, length / 0.3) * n.Confidence, 2) * n.Onset;
            int b = Math.Min(blocks - 1, (int)(Time(n.Begin) / 6));
            for (int p = Math.Max(21, n.Pitch - 16); p <= Math.Min(108, n.Pitch + 16); p++)
                bins[b, p - 21] += weight * Math.Exp(-Math.Pow(p - n.Pitch, 2) / 32);
        }
        var total = new double[88];
        for (int b = 0; b < blocks; b++)
            for (int p = 0; p < 88; p++) total[p] += bins[b, p];
        double fraction = register switch { "high" => 0.85, "mid" => 0.5, "low" => 0.15, _ => 0.6 };
        double desired = total.Sum() * fraction;
        int target = 0;
        while (target < 87 && desired > total[target]) desired -= total[target++];
        var back = new int[blocks, 88];
        var scores = new double[88];
        for (int b = 0; b < blocks; b++)
        {
            token.ThrowIfCancellationRequested();
            var next = new double[88];
            double maximum = 1e-9;
            for (int p = 0; p < 88; p++) maximum = Math.Max(maximum, bins[b, p]);
            for (int p = 0; p < 88; p++)
            {
                double local = bins[b, p] / maximum - (register == "auto" ? 0.35 : 1.6) * Math.Min(2, Math.Abs(p - target) / 18.0);
                if (b == 0) { next[p] = local; continue; }
                double best = double.NegativeInfinity;
                for (int prev = 0; prev < 88; prev++)
                {
                    double candidate = scores[prev] - 0.055 * Math.Abs(p - prev);
                    if (candidate > best) { best = candidate; back[b, p] = prev; }
                }
                next[p] = local + best;
            }
            scores = next;
        }
        var result = new int[blocks];
        int last = Array.IndexOf(scores, scores.Max());
        for (int b = blocks - 1; b >= 0; b--) { result[b] = last + 21; last = back[b, last]; }
        return result;
    }
}
