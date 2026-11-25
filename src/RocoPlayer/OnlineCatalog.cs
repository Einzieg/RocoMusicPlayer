using HtmlAgilityPack;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;

namespace RocoPlayer;

internal record OnlineSong(Guid Id, string Title, string Author, string Format, string Subtitle);
internal record SearchPage(List<OnlineSong> Songs, bool HasNext);
internal record DownloadedMidi(byte[] Bytes, string Title, Uri Source);

internal sealed class OnlineCatalog : IDisposable
{
    internal static readonly Uri Site = new("https://rocomusic.cn");
    private const string StorageHost = "c9b792ad002485adae47482f4dba6a72.r2.cloudflarestorage.com";
    private readonly HttpClient client;

    internal OnlineCatalog(HttpMessageHandler? handler = null)
    {
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RocoPlayer/0.2 (public-score-client)");
    }

    internal async Task<SearchPage> SearchAsync(string query, int page, CancellationToken token)
    {
        if (query.Length > 100 || page is < 1 or > 10000) throw new ArgumentException("搜索词或页码无效");
        var uri = new Uri(Site, $"/library?q={Uri.EscapeDataString(query.Trim())}&page={page}");
        var root = await ReadPage(uri, token);
        if (!root.Descendants().Any(n => n.HasClass("library-search")))
            throw new InvalidDataException("网站页面格式已变化，无法读取曲谱列表");
        var songs = new List<OnlineSong>();
        foreach (var card in root.Descendants("article").Where(n => n.HasClass("score-card")))
        {
            var href = Links(card).FirstOrDefault(u => u.AbsolutePath.StartsWith("/scores/", StringComparison.Ordinal));
            var title = Field(card, "score-title");
            if (href == null || !Guid.TryParse(href.AbsolutePath.Split('/').Last(), out var id) || title.Length == 0)
                throw new InvalidDataException("无法识别在线曲谱卡片");
            songs.Add(new(id, title, Field(card, "byline"), Field(card, "format-chip"), Field(card, "score-subtitle")));
        }
        if (songs.Count == 0 && root.Descendants().Any(n => n.HasClass("score-title")))
            throw new InvalidDataException("网站曲谱列表格式已变化");
        return new(songs, Links(root).Any(u => u.AbsolutePath == "/library" &&
            HttpUtility.ParseQueryString(u.Query)["page"] == (page + 1).ToString()));
    }

    internal async Task<DownloadedMidi> DownloadAsync(Guid id, CancellationToken token)
    {
        var root = await ReadPage(new Uri(Site, $"/scores/{id}"), token);
        var uri = Links(root).FirstOrDefault(u => u.AbsolutePath == $"/api/scores/{id}/file" &&
            HttpUtility.ParseQueryString(u.Query)["download"] == "1");
        var title = Clean(root.Descendants("h1").FirstOrDefault()?.InnerText);
        if (uri == null || title.Length == 0) throw new InvalidDataException("该曲谱暂无可下载的 MIDI");
        var bytes = await Fetch(uri, true, 8 * 1024 * 1024, token);
        if (!bytes.AsSpan().StartsWith("MThd"u8)) throw new InvalidDataException("下载内容不是 MIDI");
        return new(bytes, title, uri);
    }

    private async Task<HtmlNode> ReadPage(Uri uri, CancellationToken token)
    {
        var document = new HtmlDocument();
        document.LoadHtml(Encoding.UTF8.GetString(await Fetch(uri, false, 4 * 1024 * 1024, token)));
        return document.DocumentNode;
    }

    private static string Clean(string? value) => Regex.Replace(HtmlEntity.DeEntitize(value ?? ""), @"\s+", " ").Trim();
    private static string Field(HtmlNode card, string name) => Clean(card.Descendants().FirstOrDefault(n => n.HasClass(name))?.InnerText);

    private static IEnumerable<Uri> Links(HtmlNode root)
    {
        foreach (var link in root.Descendants("a"))
        {
            var href = WebUtility.HtmlDecode(link.GetAttributeValue("href", ""));
            if (Uri.TryCreate(Site, href, out var uri) && uri.GetLeftPart(UriPartial.Authority) == Site.GetLeftPart(UriPartial.Authority))
                yield return uri;
        }
    }

    private async Task<byte[]> Fetch(Uri uri, bool allowStorage, int limit, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        for (int i = 0; i < 8; i++)
        {
            if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length > 0 ||
                (uri.Host != Site.Host && !(allowStorage && uri.Host == StorageHost)))
                throw new InvalidDataException("下载重定向到了未经识别的站点");
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is Uri location)
            {
                uri = new Uri(uri, location);
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("在线文件超过大小限制");
            using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var output = new MemoryStream();
            byte[] buffer = new byte[65536];
            int count;
            while ((count = await input.ReadAsync(buffer, deadline.Token)) > 0)
            {
                if (output.Length + count > limit) throw new InvalidDataException("在线文件超过大小限制");
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }
        throw new InvalidDataException("网站重定向次数过多");
    }

    public void Dispose() => client.Dispose();
}
