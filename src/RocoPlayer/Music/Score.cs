using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RocoPlayer.Music;

[JsonConverter(typeof(NoteConverter))]
public sealed record Note(double Start, double Duration, int Pitch, int Track = 0);

public sealed record Score(string Title, IReadOnlyList<Note> Notes, double Duration,
    [property: JsonConverter(typeof(TracksConverter))] IReadOnlyDictionary<int, string>? Tracks = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int[]? PercussionTracks { get; init; }

    internal void Validate()
    {
        if (Notes is null || Notes.Count is < 1 or > 100_000 || !double.IsFinite(Duration) || Duration <= 0)
            throw new InvalidDataException("乐谱时长或音符数量无效");
        foreach (var n in Notes)
            if (n is null || !double.IsFinite(n.Start) || !double.IsFinite(n.Duration) || n.Start < 0 ||
                n.Duration <= 0 || n.Start + n.Duration > Duration + .01 || n.Pitch is < 0 or > 127)
                throw new InvalidDataException("谱库包含无效音符");
    }
}

// Version 1 libraries encode notes and track names as compact JSON rows.
internal sealed class NoteConverter : JsonConverter<Note>
{
    public override Note Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("音符必须是数组");
        reader.Read();
        double start = reader.GetDouble();
        reader.Read();
        double length = reader.GetDouble();
        reader.Read();
        int pitch = reader.GetInt32();
        reader.Read();
        int track = 0;
        if (reader.TokenType == JsonTokenType.Number)
        {
            track = reader.GetInt32();
            reader.Read();
        }
        if (reader.TokenType != JsonTokenType.EndArray) throw new JsonException("无效音符");
        return new Note(start, length, pitch, track);
    }

    public override void Write(Utf8JsonWriter writer, Note n, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(n.Start);
        writer.WriteNumberValue(n.Duration);
        writer.WriteNumberValue(n.Pitch);
        writer.WriteNumberValue(n.Track);
        writer.WriteEndArray();
    }
}

internal sealed class TracksConverter : JsonConverter<IReadOnlyDictionary<int, string>>
{
    public override bool HandleNull => true;
    public override IReadOnlyDictionary<int, string> Read(ref Utf8JsonReader r, Type type, JsonSerializerOptions options)
    {
        if (r.TokenType != JsonTokenType.StartArray) throw new JsonException("无效音轨列表");
        var tracks = new Dictionary<int, string>();
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            if (r.TokenType != JsonTokenType.StartArray || !r.Read()) throw new JsonException("无效音轨");
            var id = r.GetInt32();
            r.Read();
            tracks.Add(id, r.GetString() ?? "");
            if (!r.Read() || r.TokenType != JsonTokenType.EndArray) throw new JsonException("无效音轨");
        }
        return tracks;
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyDictionary<int, string>? tracks, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var (id, name) in tracks ?? new Dictionary<int, string> { [0] = "主旋律" })
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(id);
            writer.WriteStringValue(name);
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
    }
}

public sealed record Binding(int Pitch, string Key);

public sealed record PlayerSettings
{
    public double Speed { get; init; } = 1;
    public double Hold { get; init; } = 60;
    public double Delay { get; init; } = 3;
    public double Bpm { get; init; } = 120;
    public int Octave { get; init; } = 3;
    public string Backend { get; init; } = "DD";
    public IReadOnlyList<Binding> Mapping { get; init; } =
        new[] { 45, 52, 53, 55, 57, 59, 60, 62, 64 }.Zip("BFGHJKTYU", (p, k) => new Binding(p, k.ToString())).ToArray();

    [JsonIgnore] public IReadOnlyDictionary<int, string> KeyMap =>
        Mapping.ToDictionary(b => b.Pitch, b => b.Key.Trim().ToUpperInvariant());
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }

    internal void Validate()
    {
        if (!double.IsFinite(Speed) || Speed is < .25 or > 4 || !double.IsFinite(Hold) || Hold is < 10 or > 150 ||
            !double.IsFinite(Delay) || Delay is < 0 or > 30 || !double.IsFinite(Bpm) || Bpm is < 20 or > 300 ||
            Octave is < 0 or > 8 || Backend is not ("DD" or "Win32"))
            throw new InvalidDataException("播放设置超出范围");
        if (Mapping is null || Mapping.Count != 9 || Mapping.Any(b => b is null || b.Pitch is < 0 or > 127 ||
                b.Key is null || !Regex.IsMatch(b.Key.Trim(), "^[a-zA-Z0-9]$")) ||
            Mapping.Select(b => b.Pitch).Distinct().Count() != 9 ||
            Mapping.Select(b => b.Key.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 9)
            throw new InvalidDataException("请配置九个互不重复的音高及字母或数字键");
    }
}

public static partial class ScoreIO
{
    static readonly int[] Degrees = [0, 2, 4, 5, 7, 9, 11];
    static readonly string[] Names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
    public static string PitchName(int pitch) => $"{Names[pitch % 12]}{pitch / 12 - 1}";

    public static int ParsePitch(string text)
    {
        var match = Regex.Match(text.Trim(), @"^([A-Ga-g])([#b]?)(-?\d)$");
        if (!match.Success) throw new FormatException($"无法识别音高 {text}");
        int pitch = (1 + int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture)) * 12 +
            Degrees["CDEFGAB".IndexOf(char.ToUpperInvariant(match.Groups[1].Value[0]))] + Accidental(match.Groups[2].Value);
        return CheckPitch(pitch);
    }

    static int Accidental(string symbol) => symbol switch { "#" => 1, "b" => -1, _ => 0 };
    static int CheckPitch(int pitch) => pitch is >= 0 and <= 127 ? pitch : throw new FormatException("音高超出 MIDI 范围");

    public static Score Load(string path, PlayerSettings settings)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".mid" or ".midi") return LoadMidi(path);
        if (ext != ".txt") throw new InvalidDataException("请导入 TXT / MID / MIDI 文件");
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("文本简谱不能超过 4 MB");
        return ParseText(File.ReadAllText(path, new System.Text.UTF8Encoding(false, true)),
            settings.Bpm, Path.GetFileNameWithoutExtension(path), settings.Octave);
    }

    public static Score ParseText(string text, double bpm = 120, string title = "文本简谱", int octave = 4)
    {
        if (!double.IsFinite(bpm) || bpm is < 20 or > 300 || octave is < 0 or > 8)
            throw new ArgumentException("BPM 应为 20～300，八度应为 0～8");
        if (text.Length > 4 * 1024 * 1024) throw new InvalidDataException("文本简谱不能超过 4 MB");
        var notes = new List<Note>();
        int previous = -1, lineNumber = 0;
        double cursor = 0;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is string line)
        {
            lineNumber++;
            var comment = line.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0) line = line[..comment];
            line = line.Replace('’', '\'').Replace('，', ',');
            foreach (Match match in Regex.Matches(line, @"\[[^\[\]\r\n]+\](?::[^\s|]+)?|[^\s|]+"))
            {
                var token = match.Value;
                try
                {
                    var parts = token.Split(':', 2);
                    double duration = parts.Length == 2 ? ParseBeats(parts[1]) * 60 / bpm : 60 / bpm;
                    if (parts[0] == "-")
                    {
                        if (previous < 0) throw new FormatException("延音前必须有音符或休止符");
                        for (int i = previous; i < notes.Count; i++)
                            notes[i] = notes[i] with { Duration = notes[i].Duration + duration };
                    }
                    else
                    {
                        previous = notes.Count;
                        if (parts[0] != "0")
                        {
                            var symbol = parts[0];
                            var pitches = symbol.StartsWith('[') && symbol.EndsWith(']')
                                ? symbol[1..^1].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) : [symbol];
                            if (pitches.Length == 0) throw new FormatException("和弦不能为空");
                            foreach (var pitch in pitches.Select(p => DegreePitch(p, octave)).Distinct())
                                notes.Add(new Note(cursor, duration, pitch));
                            if (notes.Count > 100_000) throw new FormatException("乐谱最多支持 100000 个音符");
                        }
                    }
                    cursor += duration;
                }
                catch (FormatException ex) { throw new InvalidDataException($"第 {lineNumber} 行，{token}：{ex.Message}", ex); }
            }
        }
        if (notes.Count == 0) throw new InvalidDataException("乐谱中没有音符");
        return new Score(title, notes, cursor, new Dictionary<int, string> { [0] = "文本简谱" });
    }

    static double ParseBeats(string value)
    {
        var parts = value.Split('/', 2);
        double n = double.Parse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture);
        if (parts.Length == 2) n /= double.Parse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture);
        if (!double.IsFinite(n) || n is < (1d / 64) or > 256) throw new FormatException("时值必须在 1/64～256 拍之间");
        return n;
    }

    static int DegreePitch(string token, int octave)
    {
        var match = Regex.Match(token, @"^([#b]?)([1-7])([',]*)$");
        if (!match.Success) throw new FormatException($"无法识别音符 {token}");
        int octaves = match.Groups[3].Value.Sum(c => c == '\'' ? 1 : -1);
        return CheckPitch((octave + 1 + octaves) * 12 + Degrees[token[match.Groups[1].Length] - '1'] +
            Accidental(match.Groups[1].Value));
    }
}
