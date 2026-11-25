using System.Text.Json;

namespace RocoPlayer.Music;

internal static partial class Arrangement
{
    // Find the last item whose start does not follow the requested instant.
    private static int FloorIndex(double[] sorted, double instant)
    {
        int i = Array.BinarySearch(sorted, instant);
        return i >= 0 ? i : ~i - 1;
    }

    private static List<Pair> ChordBacking(Score source, List<Pair> lead, int shift, int[] keys, CancellationToken token)
    {
        var starts = lead.Select(p => p.Fitted.Start).ToArray();
        var identities = lead.Select(p => (p.Source.Start,p.Source.Pitch,p.Source.Track)).ToHashSet();
        var active = new Dictionary<int, double>();
        List<Pair> backing = [];
        foreach (var n in source.Notes.OrderBy(n => n.Start).ThenBy(n => n.Pitch).ThenByDescending(n => n.Duration).ThenBy(n => n.Track))
        {
            token.ThrowIfCancellationRequested();
            if (identities.Contains((n.Start,n.Pitch,n.Track))) continue;
            foreach (int expired in active.Where(a => a.Value <= n.Start + 1e-8).Select(a => a.Key).ToArray()) active.Remove(expired);
            if (active.Count >= 2) continue;
            int ceiling = keys[^1] + 1;
            int index = Math.Max(0, FloorIndex(starts, n.Start));
            for (int i = index; i < lead.Count && starts[i] < n.Start + n.Duration - 1e-8; i++)
            {
                var locked = lead[i].Fitted;
                if (locked.Start + locked.Duration > n.Start + 1e-8) ceiling = Math.Min(ceiling, locked.Pitch);
            }
            if (n.Duration < .02) continue;
            int? key = keys.Where(k => k < ceiling && !active.ContainsKey(k) && (k - n.Pitch - shift) % 12 == 0)
                .OrderBy(k => Math.Abs(k - n.Pitch - shift)).Select(k => (int?)k).FirstOrDefault();
            if (key is null) continue;
            backing.Add(new Pair(n, n with { Pitch = key.Value, Track = 1 }));
            active[key.Value] = n.Start + n.Duration;
        }
        return backing;
    }

    private static List<Pair> SparseBacking(Score source, List<Pair> lead, int shift, int[] keys,
        JsonElement? config, CancellationToken token)
    {
        if (config is not { ValueKind: JsonValueKind.Object } value || value.GetProperty("version").GetInt32() != 1)
            throw new InvalidDataException("稀疏伴奏配置不完整或版本不支持");
        int track = value.GetProperty("backing_track").GetInt32();
        var segments = value.GetProperty("tempo_map").EnumerateArray().Select(s => s.EnumerateArray().Select(v => v.GetDouble()).ToArray()).ToArray();
        if (segments.Length == 0 || segments.Any(s => s.Length != 3 || s.Any(v => !double.IsFinite(v)) || s[0] < 0 || s[1] < 0 || s[2] <= 0) ||
            segments[0][0] != 0 || segments[0][1] != 0) throw new InvalidDataException("稀疏伴奏速度表无效");
        for (int i = 1; i < segments.Length; i++)
        {
            var prev = segments[i - 1];
            var next = segments[i];
            if (next[0] <= prev[0] || Math.Abs(next[1] - prev[1] - (next[0] - prev[0]) * prev[2] / 1e6) > 1e-6)
                throw new InvalidDataException("稀疏伴奏速度表无效");
        }
        var beats = segments.Select(s => s[0]).ToArray();
        var times = segments.Select(s => s[1]).ToArray();
        var starts = lead.Select(p => p.Fitted.Start).ToArray();
        var identities = lead.Select(p => (p.Source.Start,p.Source.Pitch,p.Source.Track)).ToHashSet();
        var lowKeys = keys.Take(keys.Length / 2 + 1).GroupBy(k => k % 12).ToDictionary(g => g.Key, g => g.First());
        var candidates = source.Notes.Where(n => n.Track == track).OrderBy(n => n.Start).ThenBy(n => n.Pitch).ThenByDescending(n => n.Duration)
            .GroupBy(n => n.Start).Select(group => group.First()).ToArray();
        if (candidates.Length == 0) throw new InvalidDataException("稀疏伴奏音轨不存在");
        List<Pair> backing = [];
        double lastBeat = double.NegativeInfinity, lastEnd = double.NegativeInfinity;
        foreach (var n in candidates)
        {
            token.ThrowIfCancellationRequested();
            if (identities.Contains((n.Start,n.Pitch,n.Track))) continue;
            var segment = segments[Math.Max(0, FloorIndex(times,n.Start + 1e-9))];
            double beat = segment[0] + (n.Start - segment[1]) * 1e6 / segment[2];
            if (beat - lastBeat < 1 - 1e-7 || n.Start < lastEnd - 1e-8 || !lowKeys.TryGetValue(((n.Pitch + shift) % 12 + 12) % 12, out int pitch))
                continue;
            int index = Math.Max(0, FloorIndex(starts,n.Start + 1e-9));
            var current = lead[index];
            if (current.Fitted.Start > n.Start + 1e-8 || current.Fitted.Start + current.Fitted.Duration <= n.Start + 1e-8 ||
                track == current.Source.Track && current.Source.Pitch - n.Pitch < 5) continue;
            segment = segments[Math.Max(0, FloorIndex(beats,beat + .5 + 1e-9))];
            double finish = Math.Min(n.Start + n.Duration, segment[1] + (beat + .5 - segment[0]) * segment[2] / 1e6);
            for (int i = index; i < lead.Count && starts[i] < finish - 1e-8; i++)
            {
                var locked = lead[i].Fitted;
                if (locked.Start + locked.Duration <= n.Start + 1e-8) continue;
                int interval = locked.Pitch - pitch;
                if (interval < 5 || interval % 12 is not (0 or 3 or 4 or 5 or 7 or 8 or 9))
                {
                    finish = Math.Min(finish, locked.Start);
                    break;
                }
            }
            if (finish - n.Start < .08) continue;
            backing.Add(new Pair(n, new Note(n.Start,finish - n.Start,pitch,1)));
            lastBeat = beat;
            lastEnd = finish;
        }
        return backing;
    }
}
