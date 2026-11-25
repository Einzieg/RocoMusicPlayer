using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Threading;
using RocoPlayer.Music;
using RocoPlayer.Playback;

namespace RocoPlayer;

public partial class MainWindow : Window
{
    private readonly ScoreLibrary library;
    private readonly string directory;
    private readonly PlaybackEngine player = new();
    private readonly AuditionPlayer audition = new();
    private readonly PlaylistNavigator navigator = new();
    private readonly OnlineCatalog catalog;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Func<TargetWindow> captureWindow;
    private static readonly Geometry PlayGlyph = Geometry.Parse("M2,1 L13,8 L2,15 Z");
    private static readonly Geometry PauseGlyph = Geometry.Parse("M2,1 V15 M9,1 V15");
    private double playbackOffset, playbackSpeed;
    private bool performing;
    private GlobalHotkeys? hotkeys;
    private TargetWindow? target;
    private CancellationTokenSource? queue, job;
    private Task queueTask = Task.CompletedTask, jobTask = Task.CompletedTask;
    private bool ready, refreshing, switching, closing, allowClose, compact;
    private long stopVersion;
    private Rect normalBounds;
    private WindowState normalState;
    private static readonly string[] Modes = ["order", "random", "repeat_one", "repeat_all"];
    private bool Busy => job != null || queue != null || switching || closing;
    private bool PreviewOnly => SilentPreview.IsChecked == true;

    internal MainWindow(ScoreLibrary library, string directory, OnlineCatalog? catalog = null, Func<TargetWindow>? captureWindow = null)
    {
        this.library = library;
        this.directory = directory;
        this.catalog = catalog ?? new OnlineCatalog();
        this.captureWindow = captureWindow ?? TargetWindow.Capture;
        InitializeComponent();
        ModeBox.ItemsSource = BarMode.ItemsSource = CompactMode.ItemsSource = new[] { "顺序播放", "随机播放", "单曲循环", "列表循环" };
        SelectCurrentMode();
        Engine.ItemsSource = new[] { "乐器音符模型 · Basic Pitch", "频谱主旋律", "独奏单音 · YIN" };
        Register.ItemsSource = new[] { "自动", "偏高", "偏中", "偏低" };
        Register.SelectedIndex = Engine.SelectedIndex = 0;
        Backend.ItemsSource = new[] { "Win32", "DD" };
        LoadSettings();
        ready = true;
        RefreshLibrary();
        timer.Tick += (_, _) => UpdateTransport();
        timer.Start();
        SourceInitialized += (_, _) =>
        {
            if (Environment.GetEnvironmentVariable("ROCK_MUSIC_NO_HOTKEYS") != "1")
                Run(() => hotkeys = new GlobalHotkeys(Hotkey));
        };
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            timer.Stop();
            hotkeys?.Dispose();
            audition.Dispose();
            player.Dispose();
            this.catalog.Dispose();
        };
    }

    private void Hotkey(string action)
    {
        if (action == "stop") { _ = GuardAsync(StopAllAsync); return; }
        if (closing || ComponentDispatcher.IsThreadModal || TextEditor.IsVisible) return;
        if (action == "play") Run(StartPlayback);
        if (action == "pause") _ = GuardAsync(TogglePauseAsync);
    }
    private void Run(Action action)
    {
        try { action(); }
        catch (Exception error) { Status.Text = error.Message; }
    }
    private async Task GuardAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { Status.Text = "已取消"; }
        catch (Exception error) { Status.Text = error.Message; }
    }
    private void RequireIdle()
    {
        if (Busy) throw new InvalidOperationException("请先停止演奏或等待当前任务完成");
    }
    private async Task JobAsync(string message, Func<CancellationToken, Task> work)
    {
        RequireIdle();
        StopAudition();
        using var source = new CancellationTokenSource();
        job = source;
        Status.Text = message;
        UpdateTransport();
        try
        {
            jobTask = work(source.Token);
            await jobTask;
        }
        finally { jobTask = Task.CompletedTask; job = null; UpdateTransport(); }
    }
    private void StartPlayback()
    {
        RequireIdle();
        if (library.CurrentId == null) throw new InvalidOperationException("请先选择曲目");
        if (!PreviewOnly && target == null) throw new InvalidOperationException("请先点击左上角的「捕获游戏窗口」");
        StopAudition();

        ArrangedVersion.IsChecked = true;
        var source = new CancellationTokenSource();
        queue = source;
        UpdateTransport();
        queueTask = PlayQueueAsync(source, library.Settings.Backend, PreviewOnly);
    }
    private async Task PlayQueueAsync(CancellationTokenSource source, string backend, bool preview)
    {
        var token = source.Token;
        try
        {
            bool first = true;
            while (library.CurrentId is string id)
            {
                token.ThrowIfCancellationRequested();
                var item = await Task.Run(() => library.Arranged(id, token), token);
                token.ThrowIfCancellationRequested();
                RefreshLibrary();
                var s = library.Settings;
                if (first && !preview)
                {
                    Status.Text = $"正在准备 {backend} 自动演奏…";
                    await player.PrepareAsync(backend, s.KeyMap.Values, target, token);
                    token.ThrowIfCancellationRequested();
                    if (AutoCompact.IsChecked == true) SetCompact(true);
                }
                token.ThrowIfCancellationRequested();
                playbackOffset = first && cursor < item.Score.Duration ? cursor : 0;
                playbackSpeed = s.Speed;
                performing = true;
                try
                {
                    await player.PlayAsync(item.Score, s.KeyMap, backend, target, preview,
                        s.Speed, s.Hold, first ? (preview ? 0 : s.Delay) : 3, token,
                        activate: first, startPosition: playbackOffset);
                }
                finally { performing = false; }
                token.ThrowIfCancellationRequested();
                if (player.Status.State != "finished") { Status.Text = player.Status.Message; break; }
                var next = navigator.Next(visibleIds, id, library.Mode);
                if (next == null) { Status.Text = "已播放到列表末尾"; break; }
                library.Select(next);
                first = false;
            }
        }
        catch (OperationCanceledException) { Status.Text = "已停止"; }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally
        {
            queue = null;
            SetCursor(0);
            source.Dispose();
            UpdateTransport();
        }
    }
    private async Task TogglePauseAsync()
    {
        if (queue == null) return;
        var token = queue.Token;
        bool resume = player.Status.State == "paused";
        if (resume && !PreviewOnly && target != null) await target.ActivateAsync(token);
        token.ThrowIfCancellationRequested();
        player.SetPaused(!resume);
    }
    private async Task StopAllAsync()
    {
        stopVersion++;
        job?.Cancel();
        queue?.Cancel();
        StopAudition();
        player.RequestStop();
        await player.StopAsync();
        await queueTask;
        Status.Text = "已停止";
    }
    private async Task SkipAsync(bool forward)
    {
        if (job != null || switching || closing) return;
        switching = true;
        try
        {
            var active = queue != null;
            var version = stopVersion;
            queue?.Cancel();
            StopAudition();
            await player.StopAsync();
            await queueTask;
            if (version != stopVersion || closing) return;
            var id = forward ? navigator.Next(visibleIds, library.CurrentId, library.Mode, automatic: false)
                : navigator.Previous(visibleIds, library.CurrentId, library.Mode);
            if (id == null) { Status.Text = "已到列表末尾"; return; }
            library.Select(id);
            SetCursor(0);
            RefreshLibrary();
            switching = false;
            if (active) StartPlayback();
        }
        finally { switching = false; UpdateTransport(); }
    }
    private void UpdateTransport()
    {
        bool idle = !Busy;
        Navigation.IsEnabled = LibraryBrowser.IsEnabled = OnlineBody.IsEnabled = AudioBody.IsEnabled = SettingsBody.IsEnabled = idle;
        CaptureButton.IsEnabled = idle || capturing;
        Versions.IsEnabled = Seek.IsEnabled = ListenButton.IsEnabled = ExportButton.IsEnabled =
            RemoveScore.IsEnabled = SourceListen.IsEnabled = idle && detail != null;
        PlayButton.IsEnabled = CompactPlay.IsEnabled = job == null && !switching && !closing && detail != null;
        PreviousButton.IsEnabled = NextButton.IsEnabled = job == null && !switching && !closing && visibleIds.Length > 0;
        CommitText.IsEnabled = idle;
        CancelJob.Visibility = job != null && !capturing ? Visibility.Visible : Visibility.Collapsed;
        Timeline.Visibility = job != null && !compact && !capturing ? Visibility.Visible : Visibility.Collapsed;
        bool pause = queue != null && player.Status.State != "paused";
        string action = pause ? "暂停" : queue != null ? "继续" :
            PreviewOnly ? "开始预览" : "开始演奏";
        if (PlayLabel.Text != action)
        {
            PlayIcon.Data = pause ? PauseGlyph : PlayGlyph;
            PlayIcon.StrokeThickness = pause ? 2.6 : 1.6;
            PlayLabel.Text = action;
            PlayButton.ToolTip = action + (queue == null ? "（F8）" : "（F9）");
        }
        if (queue == null)
        {
            if (listening != null && !pendingAuditionSeek && audition.Progress is { } audio)
            {
                if (Seek.Maximum != audio.Total) SetTrackDuration(audio.Total);
                SetPosition(audio.Position);
                Timeline.Value = 100 * audio.Position / audio.Total;
            }
            else if (job == null && listening == null) Timeline.Value = 0;
            return;
        }
        if (!performing) return;
        var s = player.Status;
        double position = Math.Min(Seek.Maximum, playbackOffset + s.Position * playbackSpeed);
        SetPosition(position);
        Timeline.Value = Seek.Maximum <= 0 ? 0 : 100 * position / Seek.Maximum;
        Status.Text = $"{detail?.Title}  ·  {s.Message}  ·  {Clock(position)} / {TrackDuration.Text}   {string.Join(" ", s.Pressed)}";
    }
    private void SetCompact(bool value)
    {
        if (compact == value) return;
        compact = value;
        if (compact)
        {
            normalState = WindowState;
            normalBounds = normalState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            WindowState = WindowState.Normal;
            Pages.Visibility = Header.Visibility = Footer.Visibility = Visibility.Collapsed;
            CompactPanel.Visibility = Visibility.Visible;
            MinWidth = 440; MinHeight = 145; Width = 520; Height = 154;
            var desk = CurrentWorkArea();
            Left = desk.Left; Top = desk.Top;
        }
        else
        {
            Pages.Visibility = Header.Visibility = Footer.Visibility = Visibility.Visible;
            CompactPanel.Visibility = Visibility.Collapsed;
            MinWidth = 860; MinHeight = 600;
            Left = normalBounds.Left; Top = normalBounds.Top; Width = normalBounds.Width; Height = normalBounds.Height;
            WindowState = normalState;
        }
        Topmost = compact;
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose) return;
        e.Cancel = true;
        if (closing) return;
        closing = true;
        try
        {
            await StopAllAsync();
            try { await jobTask; } catch (Exception) { /* Job errors have already been reported by the initiating action. */ }
            await player.StopAsync();
            allowClose = true;
            _ = Dispatcher.BeginInvoke(Close);
        }
        catch (Exception ex) { closing = false; Status.Text = $"关闭失败，请再次停止后重试。{ex.Message}"; }
    }
    private void SelectCurrentMode()
    {
        int index = Math.Max(0, Array.IndexOf(Modes, library.Mode));
        ModeBox.SelectedIndex = BarMode.SelectedIndex = CompactMode.SelectedIndex = index;
    }
    private void ModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || sender is not ComboBox { SelectedIndex: >= 0 } box || Modes[box.SelectedIndex] == library.Mode) return;
        Run(() =>
        {
            try { library.SetMode(Modes[box.SelectedIndex]); navigator.Reset(); }
            finally { SelectCurrentMode(); }
        });
    }
    private async void PlayToggleClick(object sender, RoutedEventArgs e)
    {
        if (queue == null) Run(StartPlayback);
        else await GuardAsync(TogglePauseAsync);
    }
    private async void StopClick(object sender, RoutedEventArgs e) => await GuardAsync(StopAllAsync);
    private async void PreviousClick(object sender, RoutedEventArgs e) => await GuardAsync(() => SkipAsync(false));
    private async void NextClick(object sender, RoutedEventArgs e) => await GuardAsync(() => SkipAsync(true));
    private void CompactClick(object sender, RoutedEventArgs e) => SetCompact(!compact);
}
