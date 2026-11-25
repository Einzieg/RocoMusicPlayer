using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using RocoPlayer.Music;
using RocoPlayer.Playback;

namespace RocoPlayer;

public partial class MainWindow
{
    private sealed class MappingRow
    {
        public string Key { get; set; } = "";
        public string Pitch { get; set; } = "";
    }
    private void LoadSettings()
    {
        var s = library.Settings;
        Speed.Text = s.Speed.ToString(CultureInfo.CurrentCulture);
        Hold.Text = s.Hold.ToString(CultureInfo.CurrentCulture);
        Delay.Text = s.Delay.ToString(CultureInfo.CurrentCulture);
        Bpm.Text = s.Bpm.ToString(CultureInfo.CurrentCulture);
        Octave.Text = s.Octave.ToString();
        Backend.SelectedItem = s.Backend;
        AutoCompact.IsChecked = s.Extra?.GetValueOrDefault("auto_compact").ValueKind != JsonValueKind.False;
        Mappings.ItemsSource = s.Mapping.Select(b => new MappingRow { Pitch = b.Pitch.ToString(), Key = b.Key }).ToList();
    }
    private void PreviewChanged(object sender, RoutedEventArgs e)
    {
        if (ready) UpdateTransport();
    }
    private static double ReadNumber(TextBox field, double min, double max, string label)
    {
        if (!double.TryParse(field.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var number) ||
            !double.IsFinite(number) || number < min || number > max)
            throw new ArgumentException($"{label}应在 {min} 至 {max} 之间");
        return number;
    }
    private void SaveSettingsClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        RequireIdle();
        var extra = library.Settings.Extra == null ? new Dictionary<string, JsonElement>() : new(library.Settings.Extra);
        extra["auto_compact"] = JsonSerializer.SerializeToElement(AutoCompact.IsChecked == true);
        var rows = Mappings.Items.Cast<MappingRow>().Select(r => new Music.Binding(
            int.TryParse(r.Pitch, out int p) ? p : throw new FormatException("MIDI 音高必须为整数"), r.Key.Trim().ToUpperInvariant())).ToList();
        var settings = library.Settings with
        {
            Speed = ReadNumber(Speed, .25, 4, "速度倍率"), Hold = ReadNumber(Hold, 10, 150, "按键时长"),
            Delay = ReadNumber(Delay, 0, 30, "延迟"), Bpm = ReadNumber(Bpm, 20, 300, "简谱速度"),
            Octave = int.TryParse(Octave.Text, out int octave) && octave is >= 0 and <= 8 ? octave : throw new ArgumentException("八度应为 0 至 8 的整数"),
            Backend = (string)Backend.SelectedItem, Mapping = rows, Extra = extra
        };
        library.SetSettings(settings);
        LoadSettings();
        RefreshLibrary();
        Status.Text = "设置已保存";
    });
    private bool capturing;
    private async void CaptureClick(object sender, RoutedEventArgs e)
    {
        if (capturing) { job?.Cancel(); return; }
        if (Busy) return;
        capturing = true;
        try
        {
            await GuardAsync(() => JobAsync("请切换到游戏窗口…", async token =>
            {
                CaptureButton.ToolTip = "点击取消本次捕获";
                for (int seconds = 3; seconds > 0; seconds--)
                {
                    CaptureButton.Content = $"取消捕获 · {seconds}s";
                    Status.Text = $"请切换到游戏窗口，{seconds} 秒后捕获";
                    await Task.Delay(1000, token);
                }
                token.ThrowIfCancellationRequested();
                var candidate = captureWindow();
                target = candidate;
                TargetInfo.Text = candidate.Title;
                Status.Text = $"已捕获到 {candidate.Title}";
            }));
        }
        finally
        {
            capturing = false;
            CaptureButton.Content = target == null ? "捕获游戏窗口" : $"已捕获到 {target.Title}";
            CaptureButton.ToolTip = target == null ? "点击后，在 3 秒内切换到游戏窗口" : $"{target.Title}\n点击重新捕获游戏窗口";
            UpdateTransport();
        }
    }
    private async void CalibrationClick(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        RequireIdle();
        var window = target ?? throw new InvalidOperationException("请先捕获游戏窗口");
        var s = library.Settings;
        if (s.Backend != "DD") throw new InvalidOperationException("请先选择并保存 DD 输入后端");
        if (MessageBox.Show(this, $"将向「{window.Title}」发送九个测试键并录制默认输出。请关闭背景音乐，确认后在 3 秒内切回游戏。F10 可中止。",
            "实测键位", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        await JobAsync("请在 3 秒内切回游戏…", async token =>
        {
            await Task.Delay(3000, token);
            var folder = Path.Combine(directory, "calibration", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            var progress = new Progress<string>(message => { if (job?.Token == token) Status.Text = message; });
            var mapping = await Task.Run(() => Calibration.MeasureAsync(s, player, window, folder, progress, token), token);
            token.ThrowIfCancellationRequested();
            library.SetSettings(s with { Mapping = mapping });
            if (library.CurrentId is string id) await Task.Run(() => library.Arranged(id, token), token);
            LoadSettings();
            RefreshLibrary();
            Status.Text = "校准完成，已保存实测音高";
        });
    });
    private async void DiagnosticsClick(object sender, RoutedEventArgs e) => await GuardAsync(() => JobAsync("正在检查 DD 文件和设备…", async token =>
    {
        var report = await Task.Run(DdDiagnostics.Inspect, token);
        token.ThrowIfCancellationRequested();
        var box = new TextBox { Text = report, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(12) };
        new Window { Title = "DD 只读诊断", Owner = this, Content = box, Width = 690, Height = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner }.ShowDialog();
        Status.Text = "DD 诊断完成";
    }));
}
