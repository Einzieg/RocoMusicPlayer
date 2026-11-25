using System.Text.Encodings.Web;
using System.Text.Json;
using RocoPlayer.Audio;
using RocoPlayer.Music;

const string usage = """
    RocoPlayer.Arrange <音频文件> [--engine instrument|melody|yin] [--register auto|high|mid|low]
                       [--settings <music_settings.json 或 library.json>] [--output <目录>]
    默认使用 Basic Pitch、自动音区。默认输出目录为当前目录下的 artifacts/arrangements。
    同名结果会更新；Ctrl+C 取消。
    """;
if (args.Any(a => a is "-h" or "--help")) { Console.WriteLine(usage); return 0; }
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    var options = Options.Parse(args);
    var mapping = LoadMapping(options.Settings);
    var source = AudioTranscriber.Transcribe(options.Audio, options.Engine, options.Register,
        cancellationToken: cancellation.Token, progress: new ProgressBuckets());
    var fit = MelodyArrangement.Create(source, mapping.Keys, cancellation.Token);
    int folded = fit.Changes.Count(p => p.Source.Pitch + fit.GlobalTranspose != p.Fitted.Pitch &&
        (p.Source.Pitch + fit.GlobalTranspose - p.Fitted.Pitch) % 12 == 0);
    int substituted = fit.Changes.Count(p => (p.Source.Pitch + fit.GlobalTranspose - p.Fitted.Pitch) % 12 != 0);
    int missing = source.Notes.Count(n => !mapping.ContainsKey(n.Pitch));
    int removed = source.Notes.Count - fit.Changes.Count;
    string summary = $"全局移调 {fit.GlobalTranspose:+0;-0;+0} 半音 · 八度折叠 {folded} 音 · 替代 {substituted} 音 · 去除伴奏/过密音符 {removed} 音 · 输出 {fit.Changes.Count} 音";
    var report = new
    {
        audio = Path.GetFullPath(options.Audio),
        engine = options.Engine,
        register = options.Register,
        revision = AudioTranscriber.Revision,
        policy = "保留旋律（推荐）",
        mapping = mapping.ToDictionary(p => ScoreIO.PitchName(p.Key), p => p.Value),
        original_notes = source.Notes.Count,
        unplayable_original_notes = missing,
        adapted_notes = fit.Score.Notes.Count,
        unplayable_adapted_notes = fit.Score.Notes.Count(n => !mapping.ContainsKey(n.Pitch)),
        duration_seconds = source.Duration,
        global_transpose = fit.GlobalTranspose,
        octave_folds = folded,
        substituted_notes = substituted,
        removed_notes = removed,
        summary,
        note_changes = fit.Changes.Select(p => new { time = p.Source.Start, original_pitch = ScoreIO.PitchName(p.Source.Pitch),
            adapted_pitch = ScoreIO.PitchName(p.Fitted.Pitch), key = mapping[p.Fitted.Pitch] }),
        limitations = "可演奏率不等于识别准确率；混音音频识别可能有误，九键编配不能完整保留任意旋律。"
    };
    var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
    {
        WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });
    string stem = Path.GetFileNameWithoutExtension(options.Audio);
    ExportFiles.Save(options.Output, stem, source, fit.Score, json, cancellation.Token);
    Console.WriteLine(summary);
    Console.WriteLine($"原谱未映射音符 {missing}/{source.Notes.Count}，九键谱 {report.unplayable_adapted_notes}/{fit.Score.Notes.Count}");
    foreach (string suffix in ExportFiles.Suffixes)
        Console.WriteLine(Path.GetFullPath(Path.Combine(options.Output, stem + suffix)));
    return 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("已取消编配。"); return 130; }
catch (ArgumentException error) { Console.Error.WriteLine(error.Message); Console.Error.WriteLine(usage); return 2; }
catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }

static Dictionary<int, string> LoadMapping(string? file)
{
    if (file is null && !File.Exists("music_settings.json"))
        return new PlayerSettings().Mapping.ToDictionary(b => b.Pitch, b => b.Key);
    using var stream = File.OpenRead(file ?? "music_settings.json");
    using var document = JsonDocument.Parse(stream);
    var settings = document.RootElement;
    if (settings.TryGetProperty("settings", out var nested)) settings = nested;
    var map = new Dictionary<int, string>();
    foreach (var row in settings.GetProperty("mapping").EnumerateArray())
    {
        bool legacy = row.ValueKind == JsonValueKind.Array;
        int pitch = legacy ? ScoreIO.ParsePitch(row[0].GetString() ?? "") : row.GetProperty("pitch").GetInt32();
        string key = ((legacy ? row[1] : row.GetProperty("key")).GetString() ?? "").Trim().ToUpperInvariant();
        if (pitch is < 0 or > 127 || key.Length != 1 || !char.IsAsciiLetterOrDigit(key[0]))
            throw new InvalidDataException("映射包含无效音高或键位。");
        map[pitch] = key;
    }
    if (map.Count == 0 || map.Values.Distinct().Count() != map.Count)
        throw new InvalidDataException("映射必须包含至少一个音高，每个键位只能绑定一次。");
    return map;
}

internal sealed record Options(string Audio, string Engine, string Register, string? Settings, string Output)
{
    public static Options Parse(string[] args)
    {
        string? audio = null, settings = null;
        string engine = "instrument", register = "auto", output = Path.Combine("artifacts", "arrangements");
        bool positionalOnly = false;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (!positionalOnly && arg == "--") { positionalOnly = true; continue; }
            if (positionalOnly || !arg.StartsWith('-'))
            {
                if (audio != null) throw new ArgumentException("请指定一个音频文件。");
                audio = arg;
                continue;
            }
            var parts = arg.Split('=', 2);
            if (parts[0] is not ("--engine" or "--register" or "--settings" or "--output"))
                throw new ArgumentException($"未知选项 {parts[0]}");
            string value = parts.Length == 2 ? parts[1] :
                i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "";
            if (value.Length == 0) throw new ArgumentException($"{parts[0]} 需要一个值。");
            switch (parts[0])
            {
                case "--engine": engine = value; break;
                case "--register": register = value; break;
                case "--settings": settings = value; break;
                case "--output": output = value; break;
            }
        }
        return new(audio ?? throw new ArgumentException("缺少音频文件。"), engine, register, settings, output);
    }
}

internal sealed class ProgressBuckets : IProgress<double>
{
    private int previous = -1;
    public void Report(double value)
    {
        int bucket = (int)(value * 10);
        if (bucket == previous) return;
        Console.WriteLine($"转谱 {bucket * 10}%");
        previous = bucket;
    }
}
