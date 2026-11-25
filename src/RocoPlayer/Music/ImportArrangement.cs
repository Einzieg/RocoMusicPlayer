namespace RocoPlayer.Music;

internal static partial class Arrangement
{
    // Match RocoMusic Studio's initialAdaptOptions and adaptSong (original, auto shift, no bass).
    private static Result AdaptImport(Score source, int[] keys, CancellationToken token)
    {
        var tracks = source.Notes.GroupBy(n => n.Track).Where(group => source.PercussionTracks?.Contains(group.Key) != true)
            .OrderBy(group => group.Key).ToArray();
        if (tracks.Length == 0) throw new InvalidDataException("曲谱只有打击乐轨，请导入含旋律音轨的曲谱。");
        string[] melodyNames = ["melody", "lead", "vocal", "旋律", "主唱"];
        var track = tracks.FirstOrDefault(group => source.Tracks?.GetValueOrDefault(group.Key) is string name &&
            melodyNames.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase))) ?? tracks[0];
        var notes = track.OrderBy(n => n.Start).ThenByDescending(n => n.Pitch).ToList();
        var (pairs, shift) = FitMelody(notes, keys, token);

        // At a shared onset the web cleaner visits melody events in reverse input order.
        // Keep that first attack for each key, then trim only a previous attack of that same key.
        List<Note> cleaned = [];
        Dictionary<int, int> lastByPitch = [];
        foreach (var onset in pairs.GroupBy(p => p.Fitted.Start))
        {
            token.ThrowIfCancellationRequested();
            foreach (var pair in onset.Reverse())
            {
                var note = pair.Fitted;
                if (lastByPitch.TryGetValue(note.Pitch, out int index))
                {
                    var previous = cleaned[index];
                    if (Math.Abs(previous.Start * 1000 - note.Start * 1000) < .001) continue;
                    if (previous.Start + previous.Duration > note.Start)
                        cleaned[index] = previous with { Duration = note.Start - previous.Start };
                }
                lastByPitch[note.Pitch] = cleaned.Count;
                cleaned.Add(note);
            }
        }
        var arranged = cleaned.OrderBy(n => n.Start).ThenByDescending(n => n.Pitch).ToArray();
        var name = source.Tracks?.GetValueOrDefault(track.Key) ?? $"音轨 {track.Key + 1}";
        return new(new Score(source.Title, arranged, source.Duration, new Dictionary<int, string> { [0] = name }),
            $"九键适配 · {name} · 移调 {shift:+0;-0;0} 半音 · 输出 {arranged.Length} 音", track.Key);
    }
}
