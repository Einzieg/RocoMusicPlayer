using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using RocoPlayer.Music;

if (args.Length != 2)
{
    Console.Error.WriteLine("用法：RocoPlayer.Contour <曲库快照.json> <新的候选目录>");
    return args is ["--help"] or ["-h"] ? 0 : 2;
}
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    var source = Path.GetFullPath(args[0]);
    var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[1]));
    if (Directory.Exists(destination) || File.Exists(destination))
        throw new IOException($"输出目录已经存在：{destination}");
    var json = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };
    using var reader = new StreamReader(source, new UTF8Encoding(false, true));
    var candidate = JsonNode.Parse(reader.ReadToEnd())?.AsObject() ?? throw new InvalidDataException("曲库为空");
    if ((int?)candidate["version"] != 1) throw new InvalidDataException("不支持的曲库版本");
    if (candidate["settings"]?["mapping"] is not JsonArray) throw new InvalidDataException("曲库缺少键位映射");
    var settings = candidate["settings"]!.Deserialize<PlayerSettings>(json)!;
    settings.Validate();
    var items = candidate["items"]?.AsArray() ?? throw new InvalidDataException("缺少曲目列表");
    var report = new JsonArray();
    var ids = new HashSet<string>();
    foreach (var entry in items)
    {
        cancellation.Token.ThrowIfCancellationRequested();
        var item = entry?.AsObject() ?? throw new InvalidDataException("谱库条目为空");
        string? id = (string?)item["id"];
        if (string.IsNullOrEmpty(id) || !ids.Add(id)) throw new InvalidDataException("谱库曲目 ID 为空或重复");
        report.Add(Arrangement.Revise(item, settings, json, cancellation.Token));
    }

    var parent = Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    string temporary = parent.CreateSubdirectory(".contour-" + Guid.NewGuid().ToString("N")).FullName;
    try
    {
        foreach (var (file, node) in new (string, JsonNode)[] { ("library.json", candidate), ("report.json", report) })
        {
            using var stream = new FileStream(Path.Combine(temporary, file), FileMode.CreateNew);
            JsonSerializer.Serialize(stream, node, json);
        }
        cancellation.Token.ThrowIfCancellationRequested();
        Directory.Move(temporary, destination);
    }
    finally
    {
        // Remove only our generated files if the atomic publication failed.
        if (Directory.Exists(temporary))
        {
            File.Delete(Path.Combine(temporary, "library.json"));
            File.Delete(Path.Combine(temporary, "report.json"));
            Directory.Delete(temporary);
        }
    }
    Console.WriteLine($"已验证 {report.Count} 首曲目。候选谱库与报告位于 {destination}");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
