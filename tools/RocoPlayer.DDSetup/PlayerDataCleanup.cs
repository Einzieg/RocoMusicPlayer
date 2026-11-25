using System.Text.Json;
using System.Text.RegularExpressions;

namespace RocoPlayer.DDSetup;

internal static class PlayerDataCleanup
{
    private static readonly Regex LibraryTemporary = Pattern(@"^library\.json\.[0-9a-f]{32}\.tmp$");
    private static readonly Regex Download = Pattern(@"^(?:[0-9a-f]{32}|[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}-[0-9a-f]{16})\.mid$");
    private static readonly Regex CalibrationSession = Pattern(@"^[0-9]{8}-[0-9]{6}-[0-9]{3}$");
    private static readonly Regex CalibrationFile = Pattern(@"^(?:measurements\.json|[a-z0-9]\.wav)$");

    private static Regex Pattern(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static DirectoryInfo? DataRoot(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new IOException("数据目录必须是绝对路径。");
        var root = new DirectoryInfo(path);
        if (root.Parent is null) throw new IOException("无法将磁盘根目录用作数据目录。");
        if (!root.Exists) return null;
        for (DirectoryInfo? ancestor = root; ancestor != null; ancestor = ancestor.Parent)
            if (ancestor.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException($"无法清理链接目录：{ancestor.FullName}");

        return root;
    }

    public static void RemoveRuntimeLogs(string path)
    {
        var logs = DataRoot(path);
        if (logs is null) return;
        DeleteFiles(logs, name => name is "dd-runtime.jsonl" or "dd-runtime.jsonl.1" or "dd-runtime.jsonl.2");
        DeleteIfEmpty(logs);
        if (logs.Parent is { Name: "RocoPlayer" } application) DeleteIfEmpty(application);
    }

    public static void Remove(string path)
    {
        var root = DataRoot(path);
        if (root is null) return;
        string defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "com.rockmusic.workspace-player");
        bool isDefault = string.Equals(Path.TrimEndingDirectorySeparator(root.FullName), defaultPath, StringComparison.OrdinalIgnoreCase);
        var library = new FileInfo(Path.Combine(root.FullName, "library.json"));
        bool ownsLibrary = library.Exists && !library.Attributes.HasFlag(FileAttributes.ReparsePoint);
        if (ownsLibrary)
        {
            // Custom roots can be shared with other programs that also use the name library.json.
            if (!isDefault && !IsPlayerLibrary(library))
                throw new InvalidDataException($"无法识别此播放器谱库：{library.FullName}");
            library.Delete();
        }
        DeleteFiles(root, name => LibraryTemporary.IsMatch(name) ||
            ((isDefault || ownsLibrary) && name is ("library.json.lock" or "library.tmp")));

        var downloads = new DirectoryInfo(Path.Combine(root.FullName, "online-scores"));
        if (IsRegularDirectory(downloads))
        {
            DeleteFiles(downloads, Download.IsMatch);
            DeleteIfEmpty(downloads);
        }
        var calibration = new DirectoryInfo(Path.Combine(root.FullName, "calibration"));
        if (IsRegularDirectory(calibration))
        {
            foreach (var session in calibration.EnumerateDirectories())
            {
                if (!CalibrationSession.IsMatch(session.Name) || !IsRegularDirectory(session)) continue;
                DeleteFiles(session, CalibrationFile.IsMatch);
                DeleteIfEmpty(session);
            }
            DeleteIfEmpty(calibration);
        }
        if (isDefault) DeleteIfEmpty(root);
    }

    private static bool IsPlayerLibrary(FileInfo library)
    {
        using var stream = library.OpenRead();
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        return root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Number &&
            root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array &&
            root.TryGetProperty("settings", out var settings) && settings.ValueKind == JsonValueKind.Object;
    }

    private static bool IsRegularDirectory(DirectoryInfo dir) =>
        dir.Exists && !dir.Attributes.HasFlag(FileAttributes.ReparsePoint);

    private static void DeleteFiles(DirectoryInfo dir, Func<string, bool> owns)
    {
        foreach (var file in dir.EnumerateFiles())
            if (owns(file.Name) && !file.Attributes.HasFlag(FileAttributes.ReparsePoint)) file.Delete();
    }

    private static void DeleteIfEmpty(DirectoryInfo dir)
    {
        if (!dir.EnumerateFileSystemInfos().Any()) dir.Delete();
    }
}
