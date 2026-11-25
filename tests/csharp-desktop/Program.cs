using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RocoPlayer;
using RocoPlayer.Music;
using RocoPlayer.Playback;

internal static class Program
{
    private const string FirstTarget = "测试游戏 · 一号窗口";
    private const string SecondTarget = "测试游戏 · 第二个窗口 · 用完整标题验证重新捕获后的导航布局";

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--capture-fixture")) return ServeCaptureWindows();
        Environment.SetEnvironmentVariable("ROCK_MUSIC_NO_HOTKEYS", "1");
        var directory = Directory.CreateTempSubdirectory("roco-wpf-");
        var library = new ScoreLibrary(directory.FullName);
        var app = new Application();
        var sample = ScoreIO.ParseText("1 3 5 0 6:2 5 3 1:2", 92);
        library.Add(sample with { Title = "云间序曲" }, "云间序曲.mid");
        library.Add(sample with { Title = "白露小调" }, "白露小调.wav");
        library.Add(sample with { Title = "晨间序曲" }, "晨间序曲.txt");
        string fixture = Path.Combine(directory.FullName, "fixture.mid");
        ScoreIO.ExportMidi(ScoreIO.ParseText("1 3 5"), fixture);
        var site = new OnlineCatalog(new FakeSite(Guid.NewGuid(), File.ReadAllBytes(fixture)));
        using var targets = StartCaptureWindows();
        var handles = (targets.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("测试窗口未启动")).Split(' ').Select(s => (nint)long.Parse(s)).ToArray();
        int captureCalls = 0;
        TargetWindow Capture() => TargetWindow.FromHandle(captureCalls < handles.Length ? handles[captureCalls++] : 0);
        var window = new MainWindow(library, directory.FullName, site, Capture) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
        int result = 1;
        window.Loaded += async (_, _) =>
        {
            try
            {
                await CheckCatalog();
                Check(library.Settings.Backend == "DD" && window.PlayLabel.Text == "开始演奏", "开始演奏默认使用 DD");
                Click(window, "开始演奏");
                Check(window.Status.Text.Contains("请先点击左上角的「捕获游戏窗口」") && window.LibraryBrowser.IsEnabled, "未捕获窗口时提示操作");
                Click(window, "添加简谱");
                Check(window.TextEditor.IsVisible, "从本地谱库打开简谱编辑器");
                window.Notation.Text = "0:2 1 0 [3 5] 6:8";
                window.TextTitle.Text = "山间来信";
                Click(window, "添加到谱库");
                await Wait(() => !window.TextEditor.IsVisible && window.LibraryBrowser.IsEnabled, window);
                Check(window.SongTitle.Text == "山间来信" && library.Items.Count == 4, "简谱入库");
                var fittedPixels = RenderNotes(window.NoteRoll);
                window.OriginalVersion.IsChecked = true;
                Check(window.NoteRoll.IsVisible && window.OriginalCount.Text == "4" && window.TargetKeys.Text == "9 键" &&
                    window.ExportButton.IsVisible && window.ExportButton.IsEnabled, "直接查看音符时间轴、适配数量和导出按钮");
                Check(!fittedPixels.SequenceEqual(RenderNotes(window.NoteRoll)), "切换到原始音符时重绘音高与时值");
                if (NAudio.Wave.WaveOut.DeviceCount > 0)
                {
                    Click(window, "试听");
                    await Wait(() => window.Seek.Value > .05 && window.Seek.Maximum > library.Items.Last().Original.Duration, window);
                    window.Seek.Value = .4;
                    await Wait(() => window.Seek.Value > .45, window);
                    Click(window, "停止试听");
                    Check(window.Status.Text.Contains("已停止") && Equals(window.ListenButton.Content, "试听"), "整曲试听、拖动定位与停止");
                }
                window.ArrangedVersion.IsChecked = true;

                window.Songs.SelectedIndex = 0;
                Check(window.SongTitle.Text == "山间来信", "最新曲谱排在首位");
                window.Filter.Text = "  序曲 ";
                Check(window.Songs.Items.Count == 2 && window.SongCount.Text == "2 / 4 首曲谱", "部分曲名搜索");
                window.Songs.SelectedIndex = 0;
                Check(window.SongTitle.Text == "晨间序曲", "搜索后的选曲");
                Click(window, "下一首");
                Check(window.SongTitle.Text == "云间序曲", "搜索结果中的下一首");
                window.Filter.Text = "mIDi";
                Check(window.Songs.Items.Count == 1, "来源搜索与大小写处理");
                window.Filter.Text = "不存在";
                Check(window.Songs.Items.Count == 0 && window.EmptyResults.IsVisible, "搜索无结果");
                Click(window, "清除搜索");
                Check(window.Songs.Items.Count == 4 && window.SongTitle.Text == "云间序曲", "清除搜索保留当前曲目");
                window.SortOrder.SelectedIndex = 1;
                window.Songs.SelectedIndex = 0;
                Check(window.SongTitle.Text == "白露小调", "中文名称排序");
                Click(window, "下一首");
                Check(window.SongTitle.Text == "晨间序曲", "按名称排序后的下一首");
                window.SortOrder.SelectedIndex = 0;
                window.Songs.SelectedIndex = 0;
                Navigate(window, "应用设置");
                window.Speed.Text = "2";
                window.Backend.SelectedItem = "Win32";
                Click(window, "保存设置");
                Check(new ScoreLibrary(directory.FullName).Settings is { Speed: 2, Backend: "Win32" } && window.PlayLabel.Text == "开始演奏", "设置保存");
                window.SilentPreview.IsChecked = true;
                Navigate(window, "音频转谱");
                Check(window.AudioPath.IsVisible && window.Engine.IsVisible && window.PlayerBar.IsVisible && window.Seek.IsVisible, "音频转谱页共用底部播放器");
                window.Seek.Value = .5;
                Click(window, "开始预览");
                await Wait(() => !window.LibraryBrowser.IsEnabled && window.Seek.Value > .55, window);
                Click(window, "暂停");
                await Wait(() => window.Status.Text.Contains("已暂停"), window);
                Click(window, "继续");
                await Wait(() => window.PlayButton.ToolTip?.ToString()?.StartsWith("暂停") == true, window);
                Click(window, "停止");
                await Wait(() => window.LibraryBrowser.IsEnabled && window.Seek.Value == 0, window);
                Check(window.AudioTab.IsSelected, "使用底栏控制后保留当前页面");
                Navigate(window, "我的谱库");
                await CheckCompact(window, library, directory.FullName);
                Navigate(window, "应用设置");
                window.SilentPreview.IsChecked = false;
                window.Backend.SelectedItem = "DD";
                Click(window, "保存设置");
                Navigate(window, "我的谱库");
                Check(window.PlayLabel.Text == "开始演奏" && new ScoreLibrary(directory.FullName).Settings.Backend == "DD", "恢复 DD 实际演奏");
                Click(window, "捕获游戏窗口");
                await Wait(() => (string)window.CaptureButton.Content == $"已捕获到 {FirstTarget}", window);
                if (Environment.GetEnvironmentVariable("ROCO_TEST_SCREENSHOT") is string path) Snapshot(window, path);

                await CheckDdPlayback(window, directory.FullName);
                if (Environment.GetEnvironmentVariable("ROCO_CHECK_LOCAL_DD") == "1")
                {
                    using var engine = new PlaybackEngine();
                    await engine.PrepareAsync("DD", library.Settings.KeyMap.Values, TargetWindow.FromHandle(handles[0]), default);
                    Console.WriteLine($"PASS 本机 DD 初始化及九键映射：{DdDiagnostics.FindLibrary()}");
                }

                Navigate(window, "在线下载");
                await Wait(() => window.OnlineList.Items.Count == 1 && window.OnlineBody.IsEnabled, window);
                window.Query.Text = "测试";
                Click(window, "搜索");
                await Wait(() => window.OnlineBody.IsEnabled, window);
                window.OnlineList.SelectedIndex = 0;
                Click(window, "下载到我的谱库");
                await Wait(() => library.Items.Count == 5 && window.LibraryBrowser.IsEnabled && window.LibraryTab.IsSelected, window);
                Check(File.ReadAllBytes(library.Items.Last().Source).SequenceEqual(File.ReadAllBytes(fixture)), "在线原件落盘");
                Navigate(window, "在线下载");
                Click(window, "下载到我的谱库");
                await Wait(() => window.Status.Text.Contains("已在谱库中"), window);
                Check(library.Items.Count == 5, "重复版本只保存一次");

                Click(window, $"已捕获到 {FirstTarget}");
                Click(window, "取消捕获 · 3s");
                await Wait(() => (string)window.CaptureButton.Content == $"已捕获到 {FirstTarget}", window);
                Check(captureCalls == 1, "取消重捕应保留原目标");
                Click(window, $"已捕获到 {FirstTarget}");
                await Wait(() => (string)window.CaptureButton.Content == $"已捕获到 {SecondTarget}", window);
                Navigate(window, "应用设置");
                Check(window.TargetInfo.Text == SecondTarget && captureCalls == 2, "重新捕获与设置页同步");
                window.Width = window.MinWidth;
                window.Height = window.MinHeight;
                Navigate(window, "我的谱库");
                window.UpdateLayout();
                var lastNav = (RadioButton)window.Navigation.Children[3];
                Check(lastNav.TranslatePoint(new Point(lastNav.ActualWidth, 0), window.Header).X < window.Header.ActualWidth - 110, "长窗口名不挤占导航");
                Check(window.ListenButton.IsVisible && window.Filter.ActualWidth > 55, "较小窗口布局");
                Check(window.PlayerBar.IsAncestorOf(window.PlayButton) && window.PlayerBar.IsAncestorOf(window.Seek) &&
                    window.ListenButton.TranslatePoint(new Point(window.ListenButton.ActualWidth, 0), window.PlayerBar).X <= window.PlayerBar.ActualWidth &&
                    window.PlayButton.TranslatePoint(new Point(0, 0), window.Shell).Y >= window.Pages.TranslatePoint(new Point(0, window.Pages.ActualHeight), window.Shell).Y,
                    "最小窗口下演奏、进度和试听完整显示在页面下方");
                Click(window, $"已捕获到 {SecondTarget}");
                await Wait(() => window.Status.Text.Contains("请切到游戏窗口后再捕获"), window);
                Check((string)window.CaptureButton.Content == $"已捕获到 {SecondTarget}", "重新捕获失败应保留原目标");
                await CheckDeletion(window, library);
                if (Environment.GetEnvironmentVariable("ROCO_CHECK_LIVE_CATALOG") == "1")
                {
                    using var live = new OnlineCatalog();
                    var page = await live.SearchAsync("", 1, default);
                    Console.WriteLine($"Live catalog page 1: {page.Songs.Count} songs");
                }
                Console.WriteLine("PASS WPF navigation, import, search, DD auto performance routing, preview, audition, transport, compact view, downloads and window recapture");
                result = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { window.Close(); }
        };
        try { app.Run(window); }
        finally
        {
            targets.StandardInput.Close();
            targets.WaitForExit(5000);
            directory.Delete(recursive: true);
        }
        return result;
    }
    private static async Task CheckCompact(MainWindow window, ScoreLibrary library, string directory)
    {
        double oldWidth = window.Width, oldHeight = window.Height;
        Click(window, "置顶小窗");
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Check(window.Topmost && window.ExpandButton.IsVisible && !window.Footer.IsVisible &&
            window.CompactPanel.ActualWidth < 550 && window.CompactPlay.Content is System.Windows.Shapes.Path &&
            window.CompactPlay.ActualWidth == window.CompactPlay.ActualHeight, "置顶小窗的图标播放器");
        Check(window.CompactPanel.Background == window.PlayerBar.Background && window.CompactPlay.Background == window.PlayButton.Background &&
            window.CompactMode.Style == window.BarMode.Style, "小窗与主播放栏使用同一套背景、主色和模式菜单样式");
        string[] modes = ["order", "random", "repeat_one", "repeat_all"];
        Check(window.CompactMode.Items.Cast<string>().SequenceEqual(["顺序播放", "随机播放", "单曲循环", "列表循环"]), "小窗模式顺序");
        window.CompactMode.IsDropDownOpen = true;
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Check(window.CompactMode.ItemContainerGenerator.ContainerFromIndex(3) is ComboBoxItem { IsVisible: true },
            "在小窗中展开所有四个播放模式");
        window.CompactMode.IsDropDownOpen = false;
        for (int i = 0; i < modes.Length; i++)
        {
            window.CompactMode.SelectedIndex = i;
            Check(library.Mode == modes[i] && new ScoreLibrary(directory).Mode == modes[i] &&
                window.BarMode.SelectedIndex == i && window.ModeBox.SelectedIndex == i, "播放模式持久化及三处同步");
        }
        window.CompactMode.SelectedIndex = 0;
        if (Environment.GetEnvironmentVariable("ROCO_TEST_COMPACT_SCREENSHOT") is string path) Snapshot(window, path);
        string? selected = library.CurrentId;
        Click(window, "下一首");
        await Wait(() => library.CurrentId != selected, window);
        Click(window, "上一首");
        await Wait(() => library.CurrentId == selected, window);
        Click(window, "开始预览");
        await Wait(() => window.CompactProgress.Value > .05, window);
        Click(window, "暂停");
        await Wait(() => AutomationProperties.GetName(window.CompactPlay) == "继续", window);
        window.CompactMode.SelectedIndex = 2;
        Click(window, "继续");
        await Wait(() => AutomationProperties.GetName(window.CompactPlay) == "暂停", window);
        Click(window, "停止");
        await Wait(() => window.CompactProgress.Value == 0 && window.LibraryBrowser.IsEnabled, window);
        window.Width = window.MinWidth;
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Check(window.CompactMode.TranslatePoint(new Point(0, 0), window.CompactPanel).X >
            window.CompactPlay.TranslatePoint(new Point(window.CompactPlay.ActualWidth, 0), window.CompactPanel).X &&
            window.ExpandButton.TranslatePoint(new Point(window.ExpandButton.ActualWidth, 0), window.CompactPanel).X <= window.CompactPanel.ActualWidth,
            "最小宽度下图标和模式按钮均在小窗内");
        Click(window, "展开谱库");
        Check(!window.Topmost && window.Footer.IsVisible && window.Width == oldWidth && window.Height == oldHeight, "恢复原窗口和主播放栏");
        window.BarMode.SelectedIndex = 1;
        Check(window.CompactMode.SelectedIndex == 1 && window.ModeBox.SelectedIndex == 1, "主播放栏切换模式");
        Navigate(window, "应用设置");
        window.ModeBox.SelectedIndex = 0;
        Check(window.CompactMode.SelectedIndex == 0 && window.BarMode.SelectedIndex == 0, "设置页切换模式");
    }

    private static async Task CheckDeletion(MainWindow window, ScoreLibrary library)
    {
        var score = ScoreIO.ParseText("1 3");
        for (int i = 0; i < 38; i++)
            library.Add(score with { Title = $"定位曲目 {i:00}" }, "local.mid");
        Navigate(window, "应用设置");
        Click(window, "保存设置");
        Navigate(window, "我的谱库");
        window.Filter.Text = "定位曲目";
        window.SortOrder.SelectedIndex = 1;
        window.Songs.SelectedIndex = 18;
        window.Songs.ScrollIntoView(window.Songs.SelectedItem);
        window.UpdateLayout();
        var scroll = Descendants(window.Songs).OfType<ScrollViewer>().Single();
        scroll.ScrollToVerticalOffset(15);
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        double offset = scroll.VerticalOffset;
        var displayed = window.Songs.ItemsSource;
        Check(offset > 0 && offset < scroll.ScrollableHeight && window.SongTitle.Text == "定位曲目 18", "列表已滚动到中部");
        window.RemoveConfirmedScore(library.CurrentId!);
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Check(window.SongTitle.Text == "定位曲目 19" && window.Songs.SelectedIndex == 18 &&
            ReferenceEquals(displayed, window.Songs.ItemsSource) && Math.Abs(offset - scroll.VerticalOffset) <= 1,
            "名称排序和搜索下删除选中项，保持滚动位置并选中同一行的下一首");
        window.SortOrder.SelectedIndex = 0;
        window.Songs.SelectedIndex = 0;
        window.RemoveConfirmedScore(library.CurrentId!);
        Check(window.SongTitle.Text == "定位曲目 36" && window.Songs.SelectedIndex == 0, "下载顺序下删除首行选择下一首");
        window.Songs.SelectedIndex = window.Songs.Items.Count - 1;
        window.RemoveConfirmedScore(library.CurrentId!);
        Check(window.SongTitle.Text == "定位曲目 01" && window.Songs.SelectedIndex == window.Songs.Items.Count - 1, "删除列表末尾选择上一首");
        window.Filter.Text = "定位曲目 01";
        window.RemoveConfirmedScore(library.CurrentId!);
        Check(window.Songs.Items.Count == 0 && library.CurrentId == null && window.EmptyResults.IsVisible &&
            window.SongTitle.Text == "请选择曲目" && !window.PlayButton.IsEnabled, "删除唯一搜索结果时清空选择");
    }

    private static async Task CheckDdPlayback(MainWindow window, string directory)
    {
        // A missing explicit DLL makes a real DD request observable before any input can be sent.
        string? previous = Environment.GetEnvironmentVariable("DD_DLL_PATH");
        string missing = Path.Combine(directory, "missing-dd", "ddhid.63340.dll");
        try
        {
            Environment.SetEnvironmentVariable("DD_DLL_PATH", missing);
            Click(window, "开始演奏");
            await Wait(() => window.LibraryBrowser.IsEnabled && window.Status.Text.Contains(missing), window);
            Check(window.PlayLabel.Text == "开始演奏" && !window.Topmost, "DD 加载失败后可以原地重试");
        }
        finally { Environment.SetEnvironmentVariable("DD_DLL_PATH", previous); }
    }

    private static Process StartCaptureWindows()
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--capture-fixture");
        return Process.Start(start) ?? throw new InvalidOperationException("测试窗口未启动");
    }
    private static int ServeCaptureWindows()
    {
        var app = new Application();
        var windows = new[] { new Window { Title = FirstTarget }, new Window { Title = SecondTarget } };
        Console.WriteLine(string.Join(" ", windows.Select(w => new WindowInteropHelper(w).EnsureHandle().ToInt64())));
        Console.Out.Flush();
        _ = Task.Run(() => { Console.ReadLine(); app.Dispatcher.BeginInvoke(new Action(app.Shutdown)); });
        app.Run();
        GC.KeepAlive(windows);
        return 0;
    }
    private static void Navigate(MainWindow window, string label)
    {
        var tab = window.Navigation.Children.OfType<RadioButton>().Single(b => Equals(b.Content, label));
        Check(tab.IsEnabled && tab.IsVisible, label);
        tab.IsChecked = true;
        window.UpdateLayout();
        Check(Equals(((TabItem)window.Pages.SelectedItem).Header, label), $"导航到{label}");
    }
    private static void Snapshot(MainWindow window, string path)
    {
        window.UpdateLayout();
        var root = window.Surface;
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
    private static byte[] RenderNotes(FrameworkElement chart)
    {
        chart.UpdateLayout();
        var image = new RenderTargetBitmap((int)chart.ActualWidth, (int)chart.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(chart);
        int stride = image.PixelWidth * 4;
        var pixels = new byte[stride * image.PixelHeight];
        image.CopyPixels(pixels, stride, 0);
        return pixels;
    }
    private static async Task Wait(Func<bool> condition, MainWindow window)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.ElapsedMilliseconds < 8000) await Task.Delay(20);
        if (!condition()) throw new TimeoutException(window.Status.Text);
    }
    private static void Click(MainWindow window, string label)
    {
        window.UpdateLayout();
        var button = Descendants(window).OfType<Button>().Single(b => b.IsVisible &&
            ((b.Content as string) == label || AutomationProperties.GetName(b) == label));
        Check(button.IsEnabled, label);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            yield return child;
            foreach (var inner in Descendants(child)) yield return inner;
        }
    }
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new InvalidOperationException(label);
    }
    private static async Task CheckCatalog()
    {
        Guid id = Guid.NewGuid();
        using var catalog = new OnlineCatalog(new FakeSite(id));
        var page = await catalog.SearchAsync("测试", 1, default);
        Check(page.HasNext && page.Songs.Single().Title == "测试 & 曲谱" && page.Songs[0].Id == id, "在线乐谱解析");
        try { await catalog.DownloadAsync(id, default); }
        catch (InvalidDataException ex) when (ex.Message.Contains("重定向")) { return; }
        throw new InvalidOperationException("在线下载应拒绝跨站重定向");
    }
    private sealed class FakeSite(Guid id, byte[]? midi = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken token)
        {
            var path = req.RequestUri!.AbsolutePath;
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            if (path == "/library") response.Content = new StringContent($"""
                <form class="library-search"></form><article class="score-card">
                <a class="score-title" href="/scores/{id}">测试 &amp; 曲谱</a></article>
                <a href="/library?page=2">下一页</a>
                """);
            else if (path == $"/scores/{id}") response.Content = new StringContent($"<h1>测试</h1><a href='/api/scores/{id}/file?download=1'>MIDI</a>");
            else if (path == $"/api/scores/{id}/file")
            {
                if (midi != null) { response.Content = new ByteArrayContent(midi); return Task.FromResult(response); }
                response.StatusCode = HttpStatusCode.Redirect;
                response.Headers.Location = new Uri("https://invalid.example/score.mid");
            }
            else throw new InvalidOperationException("意外的 HTTP 请求");
            return Task.FromResult(response);
        }
    }
}
