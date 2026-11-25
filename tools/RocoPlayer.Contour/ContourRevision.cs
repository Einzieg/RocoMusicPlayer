using System.Text.Json;
using System.Text.Json.Nodes;
using RocoPlayer.Playback;

namespace RocoPlayer.Music;

// Reuse the desktop arranger's voice selection, assignment and contour fitting.
internal static partial class Arrangement
{
    private sealed record MotionCounts(int Reversals, int Plateaus);
    private static MotionCounts CountMotion(List<Pair> pairs)
    {
        int reversals = 0, plateaus = 0;
        for (int i = 1; i < pairs.Count; i++)
        {
            var a = pairs[i - 1];
            var b = pairs[i];
            if (b.Source.Start - a.Source.Start - a.Source.Duration > .7) continue;
            int input = b.Source.Pitch - a.Source.Pitch, output = b.Fitted.Pitch - a.Fitted.Pitch;
            if (input * output < 0) reversals++;
            if (input != 0 && output == 0) plateaus++;
        }
        return new(reversals, plateaus);
    }

    public static JsonObject Revise(JsonObject item, PlayerSettings settings, JsonSerializerOptions json, CancellationToken token)
    {
        var source = item["original"]?.Deserialize<Score>(json) ?? throw new InvalidDataException("缺少原谱");
        var old = item["score"]?.Deserialize<Score>(json) ?? throw new InvalidDataException("缺少九键谱");
        source.Validate();
        old.Validate();
        string texture = (string?)item["texture"] ?? "melody";
        int? track = (int?)item["melody_track"];
        var keys = settings.KeyMap.Keys.Order().ToArray();
        var report = new JsonObject { ["id"] = (string?)item["id"], ["title"] = (string?)item["title"] };
        string? skip = null;
        if (source.Notes.All(n => keys.Contains(n.Pitch))) skip = "preserved_already_nine_key";
        else if (texture is not ("melody" or "melody_bass" or "chords")) skip = "preserved_unsupported_texture";
        else if (texture == "chords")
        {
            var ordered = source.Notes.OrderBy(n => n.Start).ToArray();
            if (ordered.Zip(ordered.Skip(1), (a,b) => a.Start + a.Duration > b.Start + .03).Any(overlap => overlap))
                skip = "preserved_unverified_polyphony";
        }
        if (skip != null)
        {
            ValidateEvents(old, settings.KeyMap, token);
            report["status"] = skip;
            return report;
        }
        var (pairs, shift) = texture == "chords" ? FitChords(source, keys, token) :
            FitMelody(SelectMelody(source, ref track), keys, token);
        var before = CountMotion(pairs);
        var prior = pairs.Select(p => p.Fitted.Pitch).ToArray();
        if (before != new MotionCounts(0,0))
        {
            var (_, pitches) = FitPath(pairs.Select(p => p.Source).ToList(), keys, shift, prior, token);
            for (int i = 0; i < pairs.Count; i++) pairs[i] = pairs[i] with { Fitted = pairs[i].Fitted with { Pitch = pitches[i] } };
        }
        var after = CountMotion(pairs);
        int changes = pairs.Where((p,i) => prior[i] != p.Fitted.Pitch).Count();
        if (texture == "melody_bass")
            pairs.AddRange(SparseBacking(source, pairs, shift, keys, item["sparse_config"]?.Deserialize<JsonElement>(), token));
        var fitted = pairs.Select(p => p.Fitted).OrderBy(n => n.Start).ThenBy(n => n.Pitch).ThenBy(n => n.Track).ToArray();
        var score = old with { Notes = fitted };
        score.Validate();
        ValidateEvents(score, settings.KeyMap, token);
        bool changed = !old.Notes.SequenceEqual(fitted);
        if (changed)
        {
            item["score"]!.AsObject()["notes"] = JsonSerializer.SerializeToNode(fitted, json);
            item["note_count"] = fitted.Length;
            if (item["summary"] is JsonValue summary && summary.TryGetValue<string>(out var text))
                item["summary"] = text + " | Contour-first revision (lossy; not individually auditioned)";
            item["contour_revision"] = JsonSerializer.SerializeToNode(new { version = 1, before, after }, json);
        }
        report["status"] = changed ? "updated" : "unchanged";
        report["before"] = JsonSerializer.SerializeToNode(before, json);
        report["after"] = JsonSerializer.SerializeToNode(after, json);
        report["lead_pitch_changes"] = changes;
        return report;
    }

    private static void ValidateEvents(Score score, IReadOnlyDictionary<int, string> keys, CancellationToken token)
    {
        foreach (double speed in new[] { .5, 1, 2 })
            foreach (double hold in new[] { 10d, 60, 150 })
            {
                token.ThrowIfCancellationRequested();
                var plan = PlaybackPlan.Compile(score, keys, speed, hold);
                var held = new HashSet<string>();
                foreach (var change in plan.Events)
                    if (!(change.Down ? held.Add(change.Key) : held.Remove(change.Key)))
                        throw new InvalidDataException($"按键 {change.Key} 的按下与释放事件不匹配");
                if (held.Count != 0) throw new InvalidDataException("曲末存在未释放按键");
            }
    }
}
