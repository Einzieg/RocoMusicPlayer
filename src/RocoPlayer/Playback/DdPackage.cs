using System.IO;
using System.Text.Json;

namespace RocoPlayer.Playback;

internal static class DdPackage
{
    internal const string Bundled = "ddhid.63340.dll";
    internal static readonly string[] Runtimes = ["msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll"];
    internal static IReadOnlyDictionary<string, string> Hashes { get; } = ReadHashes();

    private static Dictionary<string, string> ReadHashes()
    {
        using var stream = typeof(DdPackage).Assembly.GetManifestResourceStream("RocoPlayer.DdPackageHashes.json")
            ?? throw new InvalidDataException("缺少 DD 软件包哈希清单");
        var hashes = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidDataException("DD 软件包哈希清单为空");
        return new(hashes, StringComparer.OrdinalIgnoreCase);
    }
}
