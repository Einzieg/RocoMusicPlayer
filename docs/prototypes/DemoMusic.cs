using System.IO;
using System.Media;

namespace RocoPlayer.Prototypes;

internal record Hit(double Start, double Seconds, int Lane, int Original);
internal record Piece(string Title, string Source, int Bpm, int Artwork, double Duration, Hit[] Hits)
{
    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.Now;
    internal static readonly int[] Pitches = [45, 52, 53, 55, 57, 59, 60, 62, 64];
    internal static readonly string[] Keys = ["B", "F", "G", "H", "J", "K", "T", "Y", "U"];
    internal static readonly string[] PitchNames = ["A2", "E3", "F3", "G3", "A3", "B3", "C4", "D4", "E4"];

    internal static List<Piece> Examples() => [
        Make("山间来信", "MIDI", 96, 0, [3,4,6,7,6,4,3,1,2,3,4,6,5,3,1,0]) with { AddedAt = SampleDate(1) },
        Make("潮汐手记", "音频", 80, 1, [1,3,4,3,1,4,6,4,3,4,7,6,4,3,2,1]) with { AddedAt = SampleDate(3) },
        Make("小星星", "简谱", 120, 2, [3,3,7,7,8,8,7,-1,6,6,5,5,4,4,3,-1]) with { AddedAt = SampleDate(6) },
        Make("落日回旋", "MIDI", 108, 3, [4,6,8,7,6,4,3,5,7,6,5,3,2,4,6,4]) with { AddedAt = SampleDate(9) }
    ];
    private static DateTimeOffset SampleDate(int daysAgo) => new(DateTime.Today.AddDays(-daysAgo).AddHours(16));

    internal static readonly Piece[] CatalogSamples = [
        Make("溪畔短歌", "MIDI", 104, 0, [2,4,3,1,4,6,5,4]),
        Make("晚灯圆舞曲", "MIDI", 90, 3, [3,5,6,7,6,5,4,1])
    ];
    internal static readonly Piece AudioSample = Make("林间练习", "音频", 84, 1, [2,3,4,6,4,3,1,0]);

    internal string PitchRange(bool original)
    {
        string[] noteNames = ["C", "C♯", "D", "E♭", "E", "F", "F♯", "G", "A♭", "A", "B♭", "B"];
        string Name(int pitch) => noteNames[pitch % 12] + (pitch / 12 - 1);
        var notes = Hits.Select(h => original ? h.Original : Pitches[h.Lane]).ToArray();
        return notes.Length == 0 ? "—" : $"{Name(notes.Min())} – {Name(notes.Max())}";
    }
    private static Piece Make(string name, string source, int bpm, int art, int[] pattern)
    {
        var hits = new List<Hit>();
        double beat = 60d / bpm;
        for (int bar = 0; bar < 16; bar++)
        {
            for (int step = 0; step < 4; step++)
            {
                int lane = pattern[(bar * 4 + step) % pattern.Length];
                if (lane < 0) continue;
                double start = (bar * 4 + step) * beat;
                bool split = art != 2 && step == 2 && bar % 2 == 0;
                hits.Add(new(start, beat * (split ? .43 : .82), lane, Pitches[lane] + 12));
                if (split)
                {
                    int next = Math.Min(lane + 1, 8);
                    hits.Add(new(start + .5 * beat, .38 * beat, next, Pitches[next] + 12));
                }
                if (step == 0 && bar % 2 == 0 && lane != 0 && art != 2)
                    hits.Add(new(start, beat * 1.65, 0, Pitches[0]));
            }
        }
        return new(name, source, bpm, art, 64 * beat, [.. hits]);
    }
    internal static Piece FromText(string name, string text)
    {
        var symbols = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (name.Trim().Length is < 1 or > 38 || symbols.Length is < 1 or > 128 ||
            symbols.Any(s => s.Length != 1 || s[0] is < '0' or > '7') || symbols.All(s => s == "0"))
            throw new FormatException("请填写曲名，并用空格分隔 1 至 7 的音符，0 表示休止。最多可输入 128 拍。");
        int[] lanes = [0, 1, 2, 3, 4, 5, 6, 7];
        var notes = symbols.Select((s, i) => (digit: s[0] - '0', i)).Where(p => p.digit != 0)
            .Select(p => new Hit(p.i * .5, .42, lanes[p.digit], 48 + new[] { 0, 2, 4, 5, 7, 9, 11 }[p.digit - 1])).ToArray();
        return new(name.Trim(), "简谱", 120, 2, symbols.Length * .5, notes);
    }
    internal static string Clock(double seconds) => TimeSpan.FromSeconds(Math.Floor(Math.Max(0, seconds))).ToString(@"mm\:ss");
}

internal sealed class ClipSynth : IDisposable
{
    private SoundPlayer? player;
    private MemoryStream? stream;
    internal void Play(Piece piece, double start, double seconds, bool original)
    {
        Stop();
        const int rate = 22050;
        int length = (int)Math.Ceiling(seconds * rate);
        var mono = new double[length];
        foreach (var n in piece.Hits.Where(n => n.Start < start + seconds && n.Start + n.Seconds > start))
        {
            double hz = 440 * Math.Pow(2, ((original ? n.Original : Piece.Pitches[n.Lane]) - 69) / 12d);
            int first = Math.Max(0, (int)((n.Start - start) * rate));
            int last = Math.Min(length, (int)((n.Start + n.Seconds - start) * rate));
            for (int i = first; i < last; i++)
            {
                double t = start + i / (double)rate - n.Start;
                double env = Math.Clamp(t / .012, 0, 1) * Math.Clamp((n.Seconds - t) / .065, 0, 1) * Math.Exp(-1.8 * t);
                double phase = Math.Tau * hz * t;
                mono[i] += env * (.86 * Math.Sin(phase) + .14 * Math.Sin(phase * 2));
            }
        }
        stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, true))
        {
            writer.Write("RIFF"u8);
            writer.Write(36 + 2 * length);
            writer.Write("WAVEfmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(rate);
            writer.Write(rate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(length * 2);
            for (int i = 0; i < length; i++)
                writer.Write((short)(Math.Tanh(mono[i] * .55) * 19000 * Math.Min(1, Math.Min(i + 1, length - i) / 205d)));
        }
        stream.Position = 0;
        player = new SoundPlayer(stream);
        player.Load();
        player.Play();
    }
    internal void Stop()
    {
        player?.Stop();
        player?.Dispose();
        player = null;
        stream?.Dispose();
        stream = null;
    }
    public void Dispose() => Stop();
}
