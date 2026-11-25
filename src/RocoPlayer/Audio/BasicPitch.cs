// Windowing, time alignment and onset decoding adapted from Spotify Basic Pitch
// (Apache-2.0, Copyright 2022 Spotify AB). See models/basic-pitch/SOURCES.json.
using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using RocoPlayer.Music;

namespace RocoPlayer.Audio;

internal static class BasicPitch
{
    private const int Rate = AudioTranscriber.Rate;
    private const int Window = 43844, Hop = 36164, Kept = 142, Padding = 3840, Width = 88;
    private const string Input = "serving_default_input_2:0";
    private const string NoteOutput = "StatefulPartitionedCall:1", OnsetOutput = "StatefulPartitionedCall:2";
    private const string ModelHash = "2C3C1D144BFA61AD236E92E169C13535C880469A12A047D4E73451F2C059A0EC";
    internal sealed record Candidate(int Begin, int End, int Pitch, double Confidence, double Onset, double Support);

    internal static double Time(int frame) =>
        frame * (256.0 / Rate) - (frame / 172) * ((172 * 256 - Window) / (double)Rate + 0.0018);

    internal static List<Note> Transcribe(float[] data, string register, bool chords, CancellationToken token, IProgress<double>? progress)
    {
        var model = Path.Combine(AppContext.BaseDirectory, "models", "basic-pitch", "nmp.onnx");
        using (var stream = File.OpenRead(model))
            if (Convert.ToHexString(SHA256.HashData(stream)) != ModelHash)
                throw new InvalidDataException("Basic Pitch 模型 SHA256 校验失败。");

        using var options = new SessionOptions { IntraOpNumThreads = 2, InterOpNumThreads = 1 };
        using var session = new InferenceSession(model, options);
        int frames = (int)(data.Length * (double)Kept / Hop);
        var noteFrames = new float[frames * Width];
        var onsetFrames = new float[frames * Width];
        for (int begin = 0, offset = -Padding; begin < frames; begin += Kept, offset += Hop)
        {
            token.ThrowIfCancellationRequested();
            var input = new DenseTensor<float>(new[] { 1, Window, 1 });
            int left = Math.Max(0, offset), count = Math.Min(data.Length, offset + Window) - left;
            data.AsSpan(left, count).CopyTo(input.Buffer.Span[(left - offset)..]);
            using var results = session.Run([NamedOnnxValue.CreateFromTensor(Input, input)], [NoteOutput, OnsetOutput]);
            foreach (var result in results)
            {
                var tensor = result.AsTensor<float>();
                if (tensor is not DenseTensor<float> dense || !tensor.Dimensions.SequenceEqual(new int[] { 1, 172, 88 }))
                    throw new InvalidDataException("Basic Pitch 模型输出的维度不正确。");
                var values = dense.Buffer.Span.Slice(15 * Width, Math.Min(Kept, frames - begin) * Width);
                foreach (var value in values)
                    if (!float.IsFinite(value)) throw new InvalidDataException("Basic Pitch 输出含有无效数值。");
                values.CopyTo((result.Name == NoteOutput ? noteFrames : onsetFrames).AsSpan(begin * Width));
            }
            progress?.Report(0.08 + 0.65 * Math.Min(1, (begin + Kept) / (double)frames));
        }

        var raw = new List<Candidate>();
        var validated = new List<Candidate>();
        var envelopes = new Dictionary<int, float[]>();
        for (int key = 0; key < Width; key++)
        {
            token.ThrowIfCancellationRequested();
            var peaks = FindPeaks(onsetFrames, key);
            if (peaks.Count != 0)
            {
                Decode(key, peaks, raw);
                var envelope = PitchEnvelope.Extract(data, key + 21, token);
                var sampled = new float[frames];
                for (int i = 0; i < frames; i++) sampled[i] = envelope.At(Time(i));
                envelopes[key + 21] = sampled;
                var kept = new List<int>();
                foreach (int peak in peaks)
                {
                    double t = Time(peak);
                    bool reject = kept.Count > 0 && t > 0.15 && t + 0.15 < (double)data.Length / Rate;
                    for (int i = Math.Max(peak - 10, kept.Count > 0 ? kept[^1] : 0); reject && i <= peak; i++)
                        if (noteFrames[i * Width + key] < 0.3) reject = false;
                    if (!reject || envelope.IsAttack(t)) kept.Add(peak);
                }
                Decode(key, kept, validated);
            }
            progress?.Report(0.73 + 0.20 * (key + 1) / Width);
        }
        validated.Sort((a, b) => a.Begin != b.Begin ? a.Begin.CompareTo(b.Begin) : a.Pitch.CompareTo(b.Pitch));
        var selected = VoiceTracker.Select(validated, raw, frames, noteFrames, envelopes, register, token, progress);
        if (chords)
        {
            var leads = selected.ToLookup(n => n.Pitch);
            foreach (var n in validated)
            {
                token.ThrowIfCancellationRequested();
                if (n.Confidence < 0.65 || n.Onset < 0.65 || n.Support < 0.8) continue;
                double start = Time(n.Begin), end = Time(n.End);
                if (!leads[n.Pitch].Any(l => l.Start < end - 1e-8 && l.Start + l.Duration > start + 1e-8))
                    selected.Add(new Note(start, end - start, n.Pitch, 1));
            }
            selected = selected.OrderBy(n => n.Start).ThenBy(n => n.Pitch).ToList();
        }
        // The published frame-time correction can put the very last frame slightly
        // after EOF. Keep note-off times within the decoded audio, even at 300 s.
        double duration = (double)data.Length / Rate;
        for (int i = 0; i < selected.Count; i++)
        {
            var note = selected[i];
            if (note.Start + note.Duration > duration)
                selected[i] = note with { Duration = duration - note.Start };
        }
        return selected;

        void Decode(int key, List<int> peaks, List<Candidate> destination)
        {
            for (int index = 0; index < peaks.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                int begin = peaks[index], limit = index + 1 == peaks.Count ? frames : peaks[index + 1];
                int end = begin + 1, below = 0;
                while (end < limit && below < 5)
                {
                    below = noteFrames[end * Width + key] < 0.3 ? below + 1 : 0;
                    end++;
                }
                end -= below;
                if (end - begin < 11 || Time(end) - Time(begin) < 0.1) continue;
                double total = 0; int supported = 0;
                for (int i = begin; i < end; i++)
                {
                    var value = noteFrames[i * Width + key];
                    total += value;
                    if (value >= 0.3) supported++;
                }
                destination.Add(new(begin, end, key + 21, total / (end - begin),
                    onsetFrames[begin * Width + key], (double)supported / (end - begin)));
                if (destination.Count > 100_000) throw new InvalidDataException("音符数量超过上限。");
            }
        }
    }

    // A plateau has one onset, centered between its edges. Stronger peaks take priority
    // within an eight-frame neighborhood, including at the beginning and end of a clip.
    private static List<int> FindPeaks(float[] onsets, int key)
    {
        int count = onsets.Length / Width;
        float At(int i) => i < 0 || i >= count ? 0 : onsets[i * Width + key];
        var candidates = new List<int>();
        for (int i = 0; i < count; i++)
        {
            float peak = At(i);
            if (peak < 0.5 || peak <= At(i - 1)) continue;
            int end = i;
            while (end + 1 < count && At(end + 1) == peak) end++;
            if (peak > At(end + 1))
            {
                // Require a 0.04 prominence before reaching a higher peak on either side.
                bool Prominent(int from, int direction)
                {
                    for (int j = from; j >= 0 && j < count; j += direction)
                    {
                        if (At(j) > peak) return false;
                        if (At(j) <= peak - 0.04) return true;
                    }
                    return true;
                }
                if (Prominent(i - 1, -1) && Prominent(end + 1, 1)) candidates.Add((i + end) / 2);
            }
            i = end;
        }
        var blocked = new bool[count];
        var kept = new List<int>();
        foreach (int peak in candidates.OrderByDescending(At))
        {
            if (blocked[peak]) continue;
            kept.Add(peak);
            Array.Fill(blocked, true, Math.Max(0, peak - 7), Math.Min(count - 1, peak + 7) - Math.Max(0, peak - 7) + 1);
        }
        kept.Sort();
        return kept;
    }
}
