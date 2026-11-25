using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RocoPlayer.Music;

namespace RocoPlayer;

public partial class MainWindow
{
    private int catalogPage;
    private string catalogQuery = "";
    private async void PageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ready && e.OriginalSource == Pages && OnlineTab.IsSelected && catalogPage == 0 && !Busy)
            await GuardAsync(() => SearchAsync(Query.Text, 1));
    }
    private Task SearchAsync(string query, int page) => JobAsync("正在读取在线乐谱…", async token =>
    {
        var result = await catalog.SearchAsync(query, page, token);
        token.ThrowIfCancellationRequested();
        catalogQuery = query;
        catalogPage = page;
        OnlineList.ItemsSource = result.Songs;
        OnlineEmpty.Text = "没有找到匹配的在线曲谱";
        OnlineEmpty.Visibility = result.Songs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PrevPage.IsEnabled = page > 1;
        NextPage.IsEnabled = result.HasNext;
        PageNumber.Text = $"第 {page} 页";
        Status.Text = $"已找到 {result.Songs.Count} 首在线曲谱";
    });
    private async void QueryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await GuardAsync(() => SearchAsync(Query.Text, 1));
    }
    private async void SearchClick(object sender, RoutedEventArgs e) => await GuardAsync(() => SearchAsync(Query.Text, 1));
    private async void OnlinePrevClick(object sender, RoutedEventArgs e) => await GuardAsync(() => SearchAsync(catalogQuery, catalogPage - 1));
    private async void OnlineNextClick(object sender, RoutedEventArgs e) => await GuardAsync(() => SearchAsync(catalogQuery, catalogPage + 1));
    private async void DownloadClick(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (((Button)sender).DataContext is not OnlineSong song) return;
        await JobAsync("正在下载并校验 MIDI…", async token =>
        {
            var midi = await catalog.DownloadAsync(song.Id, token);
            var hash = Convert.ToHexString(SHA256.HashData(midi.Bytes)).ToLowerInvariant();
            var id = song.Id.ToString();
            if (library.Items.Any(i => IsSameDownload(i, id, hash)))
            {
                Status.Text = "该版本已在谱库中";
                return;
            }
            var folder = Directory.CreateDirectory(Path.Combine(directory, "online-scores"));
            string path = Path.Combine(folder.FullName, $"{id}-{hash[..16]}.mid");
            string temporary = Path.Combine(folder.FullName, $"{Guid.NewGuid():N}.mid");
            bool created = false;
            try
            {
                await File.WriteAllBytesAsync(temporary, midi.Bytes, token);
                var score = await Task.Run(() => ScoreIO.LoadMidi(temporary) with { Title = midi.Title }, token);
                token.ThrowIfCancellationRequested();
                if (!File.Exists(path)) { File.Move(temporary, path); created = true; }
                var provenance = JsonSerializer.SerializeToElement(new { site = "https://rocomusic.cn", score_id = id, url = midi.Source.AbsoluteUri, sha256 = hash });
                await Task.Run(() => library.Add(score, path, token: token, metadata: new() { ["provenance"] = provenance }), token);
            }
            catch { if (created) File.Delete(path); throw; }
            finally { File.Delete(temporary); }
            Imported();
        });
    });
    private static bool IsSameDownload(LibraryItem item, string id, string hash) =>
        item.Extra?.TryGetValue("provenance", out var provenance) == true && provenance.ValueKind == JsonValueKind.Object &&
        provenance.TryGetProperty("score_id", out var oldId) && oldId.ToString() == id &&
        provenance.TryGetProperty("sha256", out var digest) && digest.ToString() == hash;
}
