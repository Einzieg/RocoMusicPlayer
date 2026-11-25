using System.Text.Json;

namespace RocoPlayer.Music;

internal static partial class Arrangement
{
    internal const string ImportTexture = "rocomusic";
    internal sealed record Result(Score Score, string Summary, int? LeadTrack);
    private sealed record Pair(Note Source, Note Fitted);

    public static Result Create(Score source, PlayerSettings settings, string texture, int? leadTrack,
        JsonElement? sparseConfig, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        source.Validate();
        var keys = settings.Mapping.Select(b => b.Pitch).Order().ToArray();
        if (texture == ImportTexture) return AdaptImport(source, keys, token);
        int shift;
        List<Pair> pairs;
        if (texture == "chords")
        {
            (pairs, shift) = FitChords(source, keys, token);
            if (!pairs.Zip(pairs.Skip(1), (a, b) => a.Source.Start + a.Source.Duration > b.Source.Start + .03).Any(overlap => overlap))
                RefineContour(pairs, keys, shift, token);
        }
        else if (texture is "melody" or "melody_bass" or "melody_chords")
        {
            var selected = SelectMelody(source, ref leadTrack);
            (pairs, shift) = FitMelody(selected, keys, token);
            RefineContour(pairs, keys, shift, token);
            if (texture == "melody_bass")
                pairs.AddRange(SparseBacking(source, pairs, shift, keys, sparseConfig, token));
            else if (texture == "melody_chords")
                pairs.AddRange(ChordBacking(source, pairs, shift, keys, token));
        }
        else throw new InvalidDataException("未知声部模式：" + texture);

        var title = texture switch { "chords" => "和弦适配", "melody_bass" => "旋律锁定疏低音",
            "melody_chords" => "旋律优先", _ => "乐器适配" };
        int folded = pairs.Count(p => p.Source.Pitch + shift != p.Fitted.Pitch && (p.Source.Pitch + shift - p.Fitted.Pitch) % 12 == 0);
        int approximated = pairs.Count(p => (p.Source.Pitch + shift - p.Fitted.Pitch) % 12 != 0);
        var arranged = pairs.Select(p => p.Fitted).OrderBy(n => n.Start).ThenBy(n => n.Pitch).ThenBy(n => n.Track).ToArray();
        Dictionary<int, string> tracks = new() { [0] = texture == "chords" ? "适配和弦" : "适配旋律" };
        if (texture is "melody_bass" or "melody_chords") tracks[1] = texture == "melody_bass" ? "稀疏单低音" : "简化伴奏";
        return new(new Score(source.Title + " · " + title, arranged, source.Duration, tracks),
            $"{title} · 全局移调 {shift:+0;-0;0} 半音 · 八度折叠 {folded} 音 · 替代 {approximated} 音 · 删减 {source.Notes.Count - pairs.Count} 音 · 输出 {pairs.Count} 音", leadTrack);
    }

    private static double Weight(Note note) => .5 + Math.Min(1, note.Duration);
    private static double Distance(int pitch, int key)
    {
        int distance = Math.Abs(pitch - key), chromatic = distance % 12;
        return 4 * Math.Min(chromatic, 12 - chromatic) + .12 * distance;
    }

    private static IEnumerable<int> BestShifts(List<Note> notes, int[] keys)
    {
        Dictionary<int, double> weights = [];
        foreach (var note in notes)
            weights[note.Pitch] = weights.GetValueOrDefault(note.Pitch) + .5 + Math.Min(note.Duration, 1);
        int start = Math.Max(-127, keys[0] - weights.Keys.Max() - 12), end = Math.Min(127, keys[^1] - weights.Keys.Min() + 12);
        return Enumerable.Range(start, end - start + 1)
            .OrderBy(s => weights.Aggregate(Math.Abs(s) * .005, (cost, w) => cost + keys.Min(k => Distance(w.Key + s, k)) * w.Value))
            .ThenBy(s => Math.Abs(s)).Take(6);
    }

    private static List<Note> SelectMelody(Score source, ref int? track)
    {
        var groups = source.Notes.GroupBy(n => n.Track).OrderBy(g => g.Key).ToArray();
        double Rank(IGrouping<int, Note> group)
        {
            string name = (source.Tracks?.GetValueOrDefault(group.Key) ?? "").ToLowerInvariant();
            var ns = group.OrderBy(n => n.Start).ToArray();
            double overlaps = ns.Zip(ns.Skip(1), (a,b) => a.Start + a.Duration > b.Start + .03).Count(value => value);
            double coverage = ns.Sum(n => Math.Min(.5, n.Duration)) / Math.Max(.1, source.Duration);
            var sorted = ns.Select(n => n.Pitch).Order().ToArray();
            double median = (sorted[ns.Length / 2] + sorted[(ns.Length - 1) / 2]) / 2d;
            return (new[] {"melody","lead","vocal","旋律","主唱","人声"}.Any(name.Contains) ? 3 : 0) -
                (new[] {"bass","drum","低音","鼓"}.Any(name.Contains) ? 3 : 0) - overlaps / ns.Length +
                Math.Min(1, coverage) + median / 128;
        }
        int id = track ?? groups.MaxBy(Rank)!.Key;
        track = id;
        var group = groups.FirstOrDefault(g => g.Key == id) ?? throw new InvalidDataException("选定的主旋律音轨不存在");
        List<Note> melody = [];
        foreach (var n in group.OrderBy(n => n.Start).ThenByDescending(n => n.Pitch))
        {
            if (melody.Count > 0)
            {
                var last = melody[^1];
                if (n.Start - last.Start < .08 - 1e-8) continue;
                if (last.Start + last.Duration > n.Start + 1e-8)
                {
                    if (n.Pitch < last.Pitch) continue;
                    melody[^1] = last with { Duration = n.Start - last.Start };
                }
            }
            melody.Add(n);
        }
        return melody;
    }

    private static (List<Pair>,int) FitMelody(List<Note> notes, int[] keys, CancellationToken token)
    {
        if (notes.All(n => keys.Contains(n.Pitch)))
            return (notes.Select(n => new Pair(n, n with { Track = 0 })).ToList(), 0);
        List<Pair> best = [];
        (double Cost, int AbsShift) rank = (double.PositiveInfinity,0);
        int bestShift = 0;
        foreach (int shift in BestShifts(notes, keys))
        {
            var (cost, pitches) = FitPath(notes, keys, shift, null, token);
            var candidate = (cost + Math.Abs(shift) * .005, Math.Abs(shift));
            if (candidate.CompareTo(rank) >= 0) continue;
            rank = candidate;
            bestShift = shift;
            best = notes.Select((n,i) => new Pair(n, n with { Pitch = pitches[i], Track = 0 })).ToList();
        }
        return (best, bestShift);
    }

    private static (double,int[]) FitPath(List<Note> notes, int[] keys, int shift, int[]? prior, CancellationToken token)
    {
        int m = keys.Length;
        var history = new byte[notes.Count, m];
        var costs = new double[m];
        var next = new double[m];
        for (int i = 0; i < notes.Count; i++)
        {
            if (i % 128 == 0) token.ThrowIfCancellationRequested();
            var n = notes[i];
            int source = n.Pitch + shift;
            int desired = i == 0 ? 0 : n.Pitch - notes[i - 1].Pitch;
            bool rest = i > 0 && n.Start - (notes[i - 1].Start + notes[i - 1].Duration) > .7;
            for (int j = 0; j < m; j++)
            {
                double local = prior is null ? Weight(n) * Distance(source, keys[j]) :
                    Math.Abs(keys[j] - prior[i]) * .45 + (keys[j] == prior[i] ? 0 : 2) +
                    Math.Abs(keys[j] - Math.Clamp(source, keys[0], keys[^1])) * .25;
                if (i == 0) { next[j] = local; continue; }
                double best = double.PositiveInfinity;
                for (int k = 0; k < m; k++)
                {
                    int motion = keys[j] - keys[k];
                    if (desired == 0 && motion != 0 || prior != null && !rest && motion * desired < 0) continue;
                    double transition = prior is null
                        ? Math.Abs(motion - desired) * .35 + (motion * desired < 0 ? 3 : 0) +
                          Math.Max(0, Math.Abs(motion) - Math.Max(7, Math.Abs(desired))) * .4
                        : Math.Abs(motion - desired) * .08 + (!rest && desired != 0 && motion == 0 ? 20 : 0);
                    double cost = costs[k] + transition * (rest ? .1 : 1);
                    if (cost < best) { best = cost; history[i,j] = (byte)k; }
                }
                next[j] = local + best;
            }
            (next,costs) = (costs,next);
        }
        double minimum = costs.Min();
        int cursor = Array.IndexOf(costs, minimum);
        int[] pitches = new int[notes.Count];
        for (int i = notes.Count - 1; i >= 0; i--)
        {
            pitches[i] = keys[cursor];
            cursor = history[i,cursor];
        }
        return (minimum, pitches);
    }

    private static void RefineContour(List<Pair> pairs, int[] keys, int shift, CancellationToken token)
    {
        if (pairs.All(p => keys.Contains(p.Source.Pitch))) return;
        bool repair = pairs.Zip(pairs.Skip(1), (a,b) =>
            b.Source.Start - a.Source.Start - a.Source.Duration <= .7 && b.Source.Pitch != a.Source.Pitch &&
            (b.Source.Pitch - a.Source.Pitch) * (b.Fitted.Pitch - a.Fitted.Pitch) <= 0).Any(v => v);
        if (!repair) return;
        var (_, pitches) = FitPath(pairs.Select(p => p.Source).ToList(), keys, shift,
            pairs.Select(p => p.Fitted.Pitch).ToArray(), token);
        for (int i = 0; i < pitches.Length; i++) pairs[i] = pairs[i] with { Fitted = pairs[i].Fitted with { Pitch = pitches[i] } };
    }

    private static (List<Pair>,int) FitChords(Score source, int[] keys, CancellationToken token)
    {
        var notes = source.Notes.GroupBy(n => (n.Start, n.Pitch)).Select(g => g.MaxBy(n => n.Duration)!)
            .OrderBy(n => n.Start).ThenBy(n => n.Pitch).ToList();
        List<Pair> result = [];
        int bestShift = 0;
        if (notes.All(n => keys.Contains(n.Pitch))) result.AddRange(notes.Select(n => new Pair(n, n with { Track = 0 })));
        else
        {
            var groups = new List<List<Note>>();
            var seen = new HashSet<int>();
            foreach (var attacks in notes.GroupBy(n => n.Start))
            {
                if (groups.Count == 0 || attacks.Key - groups[^1][0].Start > .04 || attacks.Any(n => seen.Contains(n.Pitch)))
                {
                    groups.Add([]);
                    seen.Clear();
                }
                groups[^1].AddRange(attacks);
                seen.UnionWith(attacks.Select(n => n.Pitch));
            }
            (double Cost, int AbsShift, int Shift) rank = (double.PositiveInfinity,0,0);
            foreach (int shift in BestShifts(notes, keys))
            {
                List<Pair> pairs = [];
                double cost = Math.Abs(shift) * .005;
                foreach (var group in groups)
                {
                    token.ThrowIfCancellationRequested();
                    if (group.Count == 1)
                    {
                        var n = group[0];
                        int key = keys.MinBy(k => Distance(n.Pitch + shift, k));
                        pairs.Add(new(n, n with { Pitch = key, Track = 0 }));
                        cost += Weight(n) * Distance(n.Pitch + shift, key);
                        continue;
                    }
                    bool excess = group.Count > keys.Length;
                    double[,] matrix = new double[Math.Min(keys.Length, group.Count), Math.Max(keys.Length, group.Count)];
                    for (int i = 0; i < group.Count; i++)
                        for (int j = 0; j < keys.Length; j++)
                            matrix[excess ? j : i, excess ? i : j] = (Distance(group[i].Pitch + shift, keys[j]) - 100) * Weight(group[i]);
                    cost += 100 * group.Sum(Weight);
                    foreach (var (row, col) in Assign(matrix))
                    {
                        cost += matrix[row,col];
                        var n = group[excess ? col : row];
                        pairs.Add(new(n, n with { Pitch = keys[excess ? row : col], Track = 0 }));
                    }
                }
                var candidate = (cost,Math.Abs(shift),shift);
                if (candidate.CompareTo(rank) < 0) { rank = candidate; result = pairs; }
            }
            bestShift = rank.Shift;
        }
        result.Sort((a,b) => (a.Fitted.Start,a.Fitted.Pitch).CompareTo((b.Fitted.Start,b.Fitted.Pitch)));
        Dictionary<int,int> previous = [];
        for (int i = 0; i < result.Count; i++)
        {
            var n = result[i].Fitted;
            if (previous.TryGetValue(n.Pitch, out int j))
            {
                var last = result[j].Fitted;
                if (last.Start + last.Duration > n.Start)
                    result[j] = result[j] with { Fitted = last with { Duration = n.Start - last.Start } };
            }
            previous[n.Pitch] = i;
        }
        return (result,bestShift);
    }

    // Rectangular Hungarian assignment. Each row requires one distinct column.
    private static IEnumerable<(int,int)> Assign(double[,] matrix)
    {
        int rows = matrix.GetLength(0), cols = matrix.GetLength(1);
        var u = new double[rows + 1];
        var v = new double[cols + 1];
        var assigned = new int[cols + 1];
        var parent = new int[cols + 1];
        for (int row = 1; row <= rows; row++)
        {
            var remaining = Enumerable.Repeat(double.PositiveInfinity, cols + 1).ToArray();
            var used = new bool[cols + 1];
            assigned[0] = row;
            int column = 0;
            do
            {
                used[column] = true;
                int active = assigned[column], next = 0;
                double delta = double.PositiveInfinity;
                for (int j = 1; j <= cols; j++)
                {
                    if (used[j]) continue;
                    double cost = matrix[active - 1,j - 1] - u[active] - v[j];
                    if (cost < remaining[j]) { remaining[j] = cost; parent[j] = column; }
                    if (remaining[j] < delta) { delta = remaining[j]; next = j; }
                }
                for (int j = 0; j <= cols; j++)
                {
                    if (used[j]) { u[assigned[j]] += delta; v[j] -= delta; }
                    else remaining[j] -= delta;
                }
                column = next;
            } while (assigned[column] != 0);
            do
            {
                int next = parent[column];
                assigned[column] = assigned[next];
                column = next;
            } while (column != 0);
        }
        for (int j = 1; j <= cols; j++) if (assigned[j] != 0) yield return (assigned[j] - 1,j - 1);
    }
}
