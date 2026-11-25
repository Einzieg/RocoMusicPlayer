namespace RocoPlayer.Music;

public sealed record MelodyArrangement(Score Score, int GlobalTranspose, IReadOnlyList<(Note Source, Note Fitted)> Changes)
{
    // The standalone arranger historically applies the melody fit before desktop contour repair.
    public static MelodyArrangement Create(Score source, IReadOnlyCollection<int> mappedPitches, CancellationToken token = default)
    {
        source.Validate();
        var pitches = mappedPitches.Distinct().Order().ToArray();
        if (pitches.Length == 0 || pitches[0] < 0 || pitches[^1] > 127)
            throw new ArgumentException("请指定有效的 MIDI 音高映射。");
        return Arrangement.FitForExport(source, pitches, token);
    }
}

internal static partial class Arrangement
{
    internal static MelodyArrangement FitForExport(Score source, int[] pitches, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        int? leadTrack = null;
        var melody = SelectMelody(source, ref leadTrack);
        var (pairs, shift) = FitMelody(melody, pitches, token);
        var score = new Score(source.Title + " · 乐器适配", pairs.Select(p => p.Fitted).ToArray(),
            source.Duration, new Dictionary<int, string> { [0] = "适配旋律" });
        return new(score, shift, pairs.Select(p => (p.Source, p.Fitted)).ToArray());
    }
}
