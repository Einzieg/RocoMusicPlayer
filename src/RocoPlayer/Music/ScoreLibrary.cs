using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RocoPlayer.Music;

public sealed record LibraryItem
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public double Duration { get; init; }
    public int NoteCount { get; init; }
    public int OriginalCount { get; init; }
    public string Source { get; init; } = "";
    public string CreatedAt { get; init; } = "";
    [JsonPropertyName("summary")] public JsonElement? StoredSummary { get; init; }
    [JsonIgnore] public string Summary => StoredSummary is { ValueKind: JsonValueKind.String } value ? value.GetString()! : "";
    public IReadOnlyList<Binding> Mapping { get; init; } = [];
    public string Texture { get; init; } = "melody";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? MelodyTrack { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public JsonElement? SparseConfig { get; init; }
    public required Score Original { get; init; }
    public required Score Score { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement> Extra { get; init; } = [];
    public override string ToString() => Title;
}

public sealed class ScoreLibrary
{
    private sealed record LibraryData
    {
        public required int Version { get; init; }
        public required IReadOnlyList<LibraryItem> Items { get; init; }
        public required PlayerSettings Settings { get; init; }
        public string Mode { get; init; } = "order";
        public string? CurrentId { get; init; }
        [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private static readonly string[] Modes = ["order", "random", "repeat_all", "repeat_one"];
    private readonly string path;
    private readonly object gate = new();
    private volatile LibraryData data;
    private byte[]? lastHash;
    public IReadOnlyList<LibraryItem> Items => data.Items;
    public PlayerSettings Settings => data.Settings;
    public string Mode => data.Mode;
    public string? CurrentId => data.CurrentId;

    public ScoreLibrary(string directory, string? legacySettingsPath = null)
    {
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "library.json");
        if (File.Exists(path))
        {
            try
            {
                using var stream = File.OpenRead(path);
                lastHash = SHA256.HashData(stream);
                stream.Position = 0;
                data = JsonSerializer.Deserialize<LibraryData>(stream, JsonOptions) ?? throw new JsonException("空文件");
                if (data.Version != 1) throw new InvalidDataException("不支持的谱库版本");
                if (data.Settings is null || data.Items is null) throw new JsonException("缺少曲库数据");
                data.Settings.Validate();
                var ids = new HashSet<string>();
                foreach (var item in data.Items)
                {
                    if (item is null || string.IsNullOrEmpty(item.Id) || !ids.Add(item.Id) || item.Original is null || item.Score is null)
                        throw new InvalidDataException("谱库条目或 ID 无效");
                    item.Original.Validate();
                    item.Score.Validate();
                }
                if (!Modes.Contains(data.Mode)) data = data with { Mode = "order" };
                if (data.CurrentId is not null && !ids.Contains(data.CurrentId)) data = data with { CurrentId = null };
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException or InvalidOperationException or FormatException)
            {
                throw new InvalidDataException($"谱库无法读取，原文件已保留：{path}。{ex.Message}", ex);
            }
            return;
        }
        var settings = new PlayerSettings();
        if (legacySettingsPath != null && File.Exists(legacySettingsPath))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(legacySettingsPath, System.Text.Encoding.UTF8));
            var mapping = json.RootElement.GetProperty("mapping").EnumerateArray().Select(row =>
                new Binding(ScoreIO.ParsePitch(row[0].GetString() ?? ""), row[1].GetString() ?? "")).ToArray();
            settings = settings with { Mapping = mapping };
            settings.Validate();
        }
        data = new() { Version = 1, Items = [], Settings = settings };
    }

    public LibraryItem Add(Score source, string sourcePath, string texture = Arrangement.ImportTexture, CancellationToken token = default,
        Dictionary<string, JsonElement>? metadata = null, int? melodyTrack = null, JsonElement? sparseConfig = null)
    {
        lock (gate)
        {
            var result = Arrangement.Create(source, data.Settings, texture, melodyTrack, sparseConfig, token);
            var item = new LibraryItem
            {
                Id = Guid.NewGuid().ToString("N"), Title = source.Title, Duration = result.Score.Duration,
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"), Source = sourcePath, Original = source, Score = result.Score,
                OriginalCount = source.Notes.Count, NoteCount = result.Score.Notes.Count, Texture = texture, SparseConfig = sparseConfig?.Clone(),
                MelodyTrack = texture is "melody_chords" or "melody_bass" ? result.LeadTrack : null,
                StoredSummary = JsonSerializer.SerializeToElement(result.Summary), Mapping = data.Settings.Mapping.ToArray(),
                Extra = metadata == null ? [] : metadata.ToDictionary(kv => kv.Key, kv => kv.Value.Clone())
            };
            token.ThrowIfCancellationRequested();
            Commit(data with { Items = [..data.Items, item], CurrentId = item.Id });
            return item;
        }
    }

    public LibraryItem Arranged(string id, CancellationToken token = default)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            var item = Find(id);
            if (item.Mapping is not null && item.Mapping.SequenceEqual(data.Settings.Mapping)) return item;
            var result = Arrangement.Create(item.Original, data.Settings, item.Texture, item.MelodyTrack, item.SparseConfig, token);
            var updated = item with { Score = result.Score, NoteCount = result.Score.Notes.Count, Duration = result.Score.Duration,
                Mapping = data.Settings.Mapping.ToArray(),
                StoredSummary = item.StoredSummary is { ValueKind: JsonValueKind.String }
                    ? JsonSerializer.SerializeToElement(result.Summary) : item.StoredSummary,
                MelodyTrack = item.MelodyTrack ??
                (item.Texture is "melody_bass" or "melody_chords" ? result.LeadTrack : null) };
            var candidate = data with { Items = data.Items.Select(i => i.Id == id ? updated : i).ToArray() };
            token.ThrowIfCancellationRequested();
            Commit(candidate);
            return updated;
        }
    }

    private LibraryItem Find(string id) => data.Items.FirstOrDefault(i => i.Id == id) ?? throw new InvalidDataException("谱子已不存在，请重新选择");

    public void Remove(string id, IReadOnlyList<string>? displayedOrder = null)
    {
        lock (gate)
        {
            Find(id);
            var remaining = data.Items.Where(i => i.Id != id).ToArray();
            string? selected = data.CurrentId;
            if (selected == id)
            {
                var order = displayedOrder?.ToList() ?? data.Items.Select(i => i.Id).ToList();
                int index = order.IndexOf(id);
                if (index >= 0) order.RemoveAt(index);
                selected = order.Count == 0 ? null : order[Math.Clamp(index, 0, order.Count - 1)];
                if (selected != null && !remaining.Any(i => i.Id == selected))
                    throw new InvalidOperationException("曲谱列表已更改，请重新选择");
            }
            Commit(data with { Items = remaining, CurrentId = selected });
        }
    }

    public void Select(string id)
    {
        lock (gate)
        {
            Find(id);
            if (data.CurrentId != id) Commit(data with { CurrentId = id });
        }
    }

    public void SetMode(string mode)
    {
        if (!Modes.Contains(mode)) throw new ArgumentException("未知播放模式");
        lock (gate) if (mode != data.Mode) Commit(data with { Mode = mode });
    }

    public void SetSettings(PlayerSettings settings)
    {
        settings.Validate();
        lock (gate) Commit(data with { Settings = settings with { Mapping = settings.Mapping.ToArray() } });
    }

    private void Commit(LibraryData updated)
    {
        // Coordinate native app instances. Detect edits made since this library was opened.
        using var writeLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 1,
            FileOptions.DeleteOnClose);
        byte[]? currentHash = null;
        if (File.Exists(path))
        {
            using var current = File.OpenRead(path);
            currentHash = SHA256.HashData(current);
        }
        if (!(lastHash ?? []).SequenceEqual(currentHash ?? [])) throw new IOException("谱库已被另一个程序修改，请重新打开后再试");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            byte[] hash;
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                JsonSerializer.Serialize(stream, updated, JsonOptions);
                stream.Flush(true);
                stream.Position = 0;
                hash = SHA256.HashData(stream);
            }
            File.Move(temporary, path, overwrite: true);
            lastHash = hash;
            data = updated;
        }
        finally { File.Delete(temporary); }
    }
}

public sealed class PlaylistNavigator
{
    readonly List<string> bag = [];
    readonly List<string> history = [];
    public void Reset() { bag.Clear(); history.Clear(); }

    public string? Next(IReadOnlyList<string> ids, string? current, string mode, bool automatic = true)
    {
        if (ids.Count == 0) return null;
        int index = IndexOf(ids,current);
        if (index < 0) return ids[0];
        if (automatic && mode == "repeat_one") return current;
        string? next;
        if (mode == "random")
        {
            bag.RemoveAll(id => id == current || !ids.Contains(id));
            if (bag.Count == 0)
            {
                var others = ids.Where(id => id != current).ToArray();
                Random.Shared.Shuffle(others);
                bag.AddRange(others);
            }
            next = bag.Count == 0 ? current : bag[^1];
            if (bag.Count > 0) bag.RemoveAt(bag.Count - 1);
        }
        else next = index + 1 < ids.Count ? ids[index + 1] : mode == "order" ? null : ids[0];
        if (next is not null)
        {
            if (history.Count == 1000) history.RemoveAt(0);
            history.Add(ids[index]);
        }
        return next;
    }

    public string? Previous(IReadOnlyList<string> ids, string? current, string mode)
    {
        if (ids.Count == 0) return null;
        while (mode == "random" && history.Count > 0)
        {
            var previous = history[^1];
            history.RemoveAt(history.Count - 1);
            if (ids.Contains(previous)) return previous;
        }
        var index = IndexOf(ids,current);
        return index > 0 ? ids[index - 1] : mode == "order" ? ids[0] : ids[^1];
    }

    static int IndexOf(IReadOnlyList<string> ids, string? id)
    {
        for (int i = 0; i < ids.Count; i++) if (ids[i] == id) return i;
        return -1;
    }
}
