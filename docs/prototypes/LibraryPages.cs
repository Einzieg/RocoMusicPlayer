using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using RocoPlayer.Playback;
using static RocoPlayer.Prototypes.Ui;

namespace RocoPlayer.Prototypes;

public sealed partial class PrototypeWindow
{
    private enum LibraryTab { Scores, Downloads, Transcription, Settings }

    private static readonly string[] PageNames = ["我的谱库", "在线下载", "音频转谱", "应用设置"];
    private static readonly StringComparer TitleOrder = StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), true);
    private readonly Dictionary<Piece, int> importedSamples = [];
    private readonly DispatcherTimer captureTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private LibraryTab libraryTab;
    private bool sortByName, showAudioResult;
    private int captureSeconds;
    private TargetWindow? capturedWindow;
    private string captureMessage = "";
    private Button? captureButton;
    private TextBlock? captureLabel, captureInfo;
    private TextBlock? settingsTarget;

    private int[] LibraryOrder()
    {
        var newest = Enumerable.Range(0, pieces.Count)
            .OrderByDescending(i => pieces[i].AddedAt).ThenByDescending(i => i);
        return (sortByName ? newest.OrderBy(i => pieces[i].Title, TitleOrder) : newest).ToArray();
    }

    private void Neighbor(int direction)
    {
        if (concept != 0)
        {
            Choose(State.Index + direction);
            return;
        }
        var order = LibraryOrder();
        int position = Array.IndexOf(order, State.Index);
        Choose(order[(position + direction + order.Length) % order.Length]);
    }

    private UIElement LibraryView()
    {
        var screen = GridOf(false, "auto", "*");
        At(screen, LibraryNavigation());
        UIElement body = libraryTab switch
        {
            LibraryTab.Downloads => DownloadsPage(),
            LibraryTab.Transcription => TranscriptionPage(),
            LibraryTab.Settings => PreferencesPage(),
            _ => LocalLibrary()
        };
        At(screen, body, 1);
        return screen;
    }

    private void Navigate(LibraryTab page)
    {
        if (libraryTab == page) return;
        Stop(false);
        libraryTab = page;
        Draw();
    }

    private UIElement LibraryNavigation()
    {
        var nav = GridOf(true, "auto", "23", "auto", "*", "auto");
        nav.Height = 62;
        nav.Margin = new(24, 0, 24, 0);
        captureLabel = Text("捕获游戏窗口", 13, Accent, true);
        var targetIcon = Text("⌖", 17, Accent, face: new FontFamily("Segoe UI Symbol"));
        captureButton = Button("捕获游戏窗口", Row(Space(targetIcon, 0, 0, 7), captureLabel),
            ToggleCapture, "#E6EEE2", 6, new(11, 8, 11, 8), "#CEDBCA");
        captureButton.VerticalAlignment = VerticalAlignment.Center;
        At(nav, captureButton);

        var tabs = Row();
        foreach (var tab in Enum.GetValues<LibraryTab>())
        {
            var name = PageNames[(int)tab];
            bool selected = tab == libraryTab;
            var entry = Button(name, Text(name, 14, selected ? Accent : Sub, selected),
                () => Navigate(tab), "Transparent", 4, new(12, 8, 12, 8));
            AutomationProperties.SetItemStatus(entry, selected ? "当前页面" : "");
            tabs.Children.Add(new Border
            {
                Child = entry,
                BorderBrush = selected ? Brush(Accent) : Brushes.Transparent,
                BorderThickness = new Thickness(0, 0, 0, 2),
                Margin = new Thickness(0, 0, 3, 0)
            });
        }
        At(nav, tabs, 0, 2);
        At(nav, Brand(Ink), 0, 4);
        captureInfo = Text("", 12, Sub);
        captureInfo.Margin = new(24, 0, 24, 8);
        SetCaptureIndicators();
        return new Border
        {
            Child = Stack(nav, captureInfo),
            BorderBrush = Brush(Stroke),
            BorderThickness = new Thickness(0, 0, 0, 1)
        };
    }

    private void ToggleCapture()
    {
        if (captureSeconds != 0)
        {
            CancelCapture();
            return;
        }
        captureSeconds = 3;
        captureTimer.Start();
        SetCaptureIndicators();
    }

    private void CancelCapture()
    {
        if (captureSeconds == 0) return;
        captureTimer.Stop();
        captureSeconds = 0;
        captureMessage = "已取消本次捕获";
        SetCaptureIndicators();
    }

    private void CaptureTick(object? sender, EventArgs e)
    {
        if (captureSeconds <= 0) return;
        if (--captureSeconds == 0)
        {
            captureTimer.Stop();
            try
            {
                capturedWindow = TargetWindow.Capture();
                captureMessage = "已捕获 · " + capturedWindow.Title;
            }
            catch (InvalidOperationException error)
            {
                captureMessage = error.Message;
            }
        }
        SetCaptureIndicators();
    }

    private void SetCaptureIndicators()
    {
        if (captureLabel == null || captureButton == null || captureInfo == null) return;
        bool pending = captureSeconds > 0;
        captureLabel.Text = pending ? $"取消捕获 · {captureSeconds}s" : "捕获游戏窗口";
        AutomationProperties.SetName(captureButton, pending ? "取消捕获" : "捕获游戏窗口");
        captureButton.ToolTip = pending ? "取消本次捕获" : "点击后，在 3 秒内切换到游戏窗口";
        captureInfo.Text = pending ? $"请切换到游戏窗口，{captureSeconds} 秒后捕获" : captureMessage;
        captureInfo.ToolTip = captureInfo.Text;
        captureInfo.Visibility = captureInfo.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (settingsTarget != null) settingsTarget.Text = TargetDescription();
    }

    private string TargetDescription() => capturedWindow == null ? "尚未捕获" :
        capturedWindow.State == "closed" ? "窗口已关闭" : capturedWindow.Title;

    private static ComboBox Choice(string label, string[] options, int selected, Action<int> change)
    {
        var box = new ComboBox
        {
            ItemsSource = options, SelectedIndex = selected, MinWidth = 104,
            Padding = new Thickness(7, 4, 7, 4), VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(box, label);
        box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0) change(box.SelectedIndex); };
        return box;
    }

    private Grid ScoreDetails()
    {
        var details = GridOf(false, "auto", "auto");
        AutomationProperties.SetName(details, "曲谱详情");
        At(details, Text("曲谱详情", 15, Ink, true));
        var fields = GridOf(true, "*", "*");
        fields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        fields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        void Field(int row, int col, string label, string value)
        {
            At(fields, Space(Stack(Text(label, 12, Sub), Space(Text(value, 14, Ink, true), 0, 2)), 0, 9, 6), row, col);
        }
        Field(0, 0, "音符数", $"{Current.Hits.Length} 个");
        Field(0, 1, "当前音域", Current.PitchRange(State.Original));
        Field(1, 0, "使用琴键", $"{Current.Hits.Select(h => h.Lane).Distinct().Count()} / 9");
        Field(1, 1, "入库日期", Current.AddedAt.ToLocalTime().ToString("yyyy.MM.dd", CultureInfo.InvariantCulture));
        At(details, fields, 1);
        return details;
    }

    private StackPanel PageBody(LibraryTab tab)
    {
        var panel = Stack(Text(PageNames[(int)tab], 29, Ink, true));
        panel.Margin = new(30, 27, 30, 20);
        return panel;
    }

    private UIElement DownloadsPage()
    {
        var panel = PageBody(LibraryTab.Downloads);
        panel.Children.Add(Space(Text("示例曲库", 13, Sub), 0, 8, 0, 16));
        foreach (var sample in Piece.CatalogSamples)
        {
            var row = GridOf(true, "*", "auto");
            At(row, Stack(Text(sample.Title, 17, Ink, true),
                Space(Text($"MIDI   ·   {sample.Bpm} BPM   ·   {Piece.Clock(sample.Duration)}", 13, Sub), 0, 3)));
            At(row, SampleButton(sample, "下载"), 0, 1);
            panel.Children.Add(new Border
            {
                Child = row, Padding = new Thickness(0, 13, 0, 13),
                BorderBrush = Brush(Stroke), BorderThickness = new Thickness(0, 0, 0, 1)
            });
        }
        return panel;
    }

    private UIElement TranscriptionPage()
    {
        var panel = PageBody(LibraryTab.Transcription);
        var sample = Piece.AudioSample;
        panel.Children.Add(Space(Text("示例音频", 13, Sub), 0, 12));
        panel.Children.Add(Space(Text(sample.Title + ".wav", 21, Ink, true), 0, 5));
        panel.Children.Add(Space(Text($"{Piece.Clock(sample.Duration)}   ·   WAV", 13, Sub), 0, 4, 0, 15));
        if (showAudioResult)
        {
            panel.Children.Add(Space(Text("转谱结果", 16, Ink, true), 0, 12));
            panel.Children.Add(Space(Text($"{sample.Bpm} BPM   ·   {sample.Hits.Length} 个音符   ·   {sample.PitchRange(false)}", 14, Sub), 0, 6, 0, 12));
            var add = SampleButton(sample, "加入谱库");
            add.HorizontalAlignment = HorizontalAlignment.Left;
            panel.Children.Add(add);
        }
        else
        {
            var show = Labeled("查看转谱结果", "\uE8D6", () => { showAudioResult = true; Draw(); }, Accent, "#FAFCF4");
            show.HorizontalAlignment = HorizontalAlignment.Left;
            panel.Children.Add(show);
        }
        return panel;
    }

    private Button SampleButton(Piece sample, string verb)
    {
        bool added = importedSamples.ContainsKey(sample);
        string label = added ? "查看曲谱" : verb;
        string glyph = added ? "\uE8A5" : verb == "下载" ? "\uE896" : "\uE710";
        var button = Labeled(label, glyph, () =>
        {
            if (importedSamples.TryGetValue(sample, out int existing))
            {
                libraryTab = LibraryTab.Scores;
                Choose(existing);
                return;
            }
            importedSamples.Add(sample, pieces.Count);
            pieces.Add(sample with { AddedAt = DateTimeOffset.Now });
            Draw();
        }, added ? "Transparent" : "#E3EDDF", Accent, Stroke);
        AutomationProperties.SetName(button, label + " " + sample.Title);
        return button;
    }

    private UIElement PreferencesPage()
    {
        var panel = PageBody(LibraryTab.Settings);
        var options = GridOf(true, "*", "auto");
        foreach (var _ in Enumerable.Range(0, 3))
            options.RowDefinitions.Add(new RowDefinition { Height = new GridLength(58) });
        At(options, Text("预览速度", 15, Ink, true));
        double[] speeds = [.75, 1, 1.25, 1.5];
        At(options, Choice("预览速度", ["0.75×", "1×", "1.25×", "1.5×"], Array.IndexOf(speeds, State.Speed), i => State.Speed = speeds[i]), 0, 1);
        At(options, Text("试听版本", 15, Ink, true), 1);
        At(options, Choice("试听版本", ["九键编配", "原谱"], State.Original ? 1 : 0, i => State.Original = i == 1), 1, 1);
        At(options, Text("目标窗口", 15, Ink, true), 2);
        settingsTarget = Text(TargetDescription(), 14, Sub);
        settingsTarget.MaxWidth = 300;
        At(options, settingsTarget, 2, 1);
        panel.Children.Add(Space(options, 0, 14));
        return panel;
    }
}
