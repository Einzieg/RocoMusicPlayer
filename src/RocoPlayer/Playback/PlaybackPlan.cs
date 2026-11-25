using RocoPlayer.Music;

namespace RocoPlayer.Playback;

internal sealed record KeyChange(double Time, string Key, bool Down);
internal sealed record PlaybackPlan(KeyChange[] Events, double Duration)
{
    public static PlaybackPlan Compile(Score score, IReadOnlyDictionary<int, string> mapping, double speed, double holdMs,
        double startPosition = 0)
    {
        if (!double.IsFinite(speed) || speed is < 0.25 or > 4)
            throw new ArgumentException("播放速度必须在 0.25～4 倍之间");
        if (!double.IsFinite(holdMs) || holdMs is < 10 or > 150)
            throw new ArgumentException("按键保持时间必须在 10～150 毫秒之间");
        if (!double.IsFinite(score.Duration) || score.Duration <= 0 || score.Notes.Count == 0)
            throw new ArgumentException("乐谱中没有可演奏音符");
        if (!double.IsFinite(startPosition) || startPosition < 0 || startPosition >= score.Duration)
            throw new ArgumentException("播放起点必须在曲目范围内");

        var hits = new Dictionary<string, SortedDictionary<double, double>>();
        foreach (var note in score.Notes)
        {
            if (!mapping.TryGetValue(note.Pitch, out var key))
                throw new ArgumentException($"音高 {note.Pitch} 没有对应键位，请先适配乐谱");
            key = NormalizeKey(key);
            if (!double.IsFinite(note.Start) || note.Start < 0 || !double.IsFinite(note.Duration) || note.Duration <= 0)
                throw new ArgumentException("乐谱包含无效音符时值");
            double remaining = note.Duration - Math.Max(0, startPosition - note.Start);
            if (remaining <= 0) continue;
            if (!hits.TryGetValue(key, out var starts))
                hits[key] = starts = new();
            double time = Math.Max(0, note.Start - startPosition) / speed;
            starts[time] = Math.Max(starts.GetValueOrDefault(time), Math.Min(holdMs / 1000, remaining / speed * 0.8));
        }

        var events = new List<KeyChange>();
        foreach (var (key, starts) in hits)
        {
            var times = starts.Keys.ToArray();
            for (int i = 0; i < times.Length; i++)
            {
                double time = times[i], length = starts[time];
                if (i + 1 < times.Length)
                    length = Math.Min(length, (times[i + 1] - time) * 0.8);
                events.Add(new(time, key, true));
                events.Add(new(time + length, key, false));
            }
        }
        var sorted = events.OrderBy(e => e.Time).ThenBy(e => e.Down).ThenBy(e => e.Key, StringComparer.Ordinal).ToArray();
        double duration = (score.Duration - startPosition) / speed;
        return new(sorted, sorted.Length == 0 ? duration : Math.Max(duration, sorted[^1].Time));
    }

    public static string NormalizeKey(string key)
    {
        var upper = key.Trim().ToUpperInvariant();
        if (upper.Length != 1 || !char.IsAsciiLetterOrDigit(upper[0]))
            throw new ArgumentException("演奏键位必须是单个英文字母或数字");
        return upper;
    }
}
