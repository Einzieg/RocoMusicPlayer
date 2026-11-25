using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using static RocoPlayer.Prototypes.Ui;

namespace RocoPlayer.Prototypes;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var application = new Application();
        application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/RocoPlayer.Prototypes;component/Theme.xaml", UriKind.Relative) });
        application.Run(new PrototypeWindow());
    }
}

public sealed partial class PrototypeWindow : Window
{
    internal static readonly string[] Concepts = ["松色谱库", "暗场编配台", "唱片听室"];
    private sealed class Scene
    {
        internal int Index;
        internal double Position;
        internal bool Original;
        internal double Speed = 1;
    }
    private readonly Scene[] scenes = [new(), new(), new()];
    private readonly List<Piece> pieces = Piece.Examples();
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromMilliseconds(70) };
    private readonly Stopwatch stopwatch = new();
    private readonly ClipSynth audio = new();
    private readonly Grid root = GridOf(false, "*", "55");
    private readonly ContentControl stage = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private int concept;
    private bool active, listening, mini, updating;
    private double basePosition, end, runSpeed;
    private Rect expandedBounds;
    private WindowState expandedState;
    private Slider? scrub;
    private TextBlock? elapsed, playGlyph, status, listenLabel;
    private NoteRoll? roll;
    private readonly List<Border> keycaps = [];
    private Scene State => scenes[concept];
    private Piece Current => pieces[State.Index];
    private string Ink => concept switch { 0 => "#253E34", 1 => "#E8F0F3", _ => "#402E36" };
    private string Sub => concept switch { 0 => "#64746A", 1 => "#A5AFBA", _ => "#776871" };
    private string Accent => concept switch { 0 => "#315A45", 1 => "#A2E5CB", _ => "#713F50" };
    private string Paper => concept switch { 0 => "#F8F8F1", 1 => "#1B2027", _ => "#F5EDE7" };
    private string Stroke => concept switch { 0 => "#D8DED3", 1 => "#3B444E", _ => "#DCCFC7" };
    private string Feedback => "无按键预览";

    public PrototypeWindow()
    {
        Title = "RocoPlayer · 界面原型";
        Width = 1080;
        Height = 746;
        MinWidth = 868;
        MinHeight = 680;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = Sans;
        UseLayoutRounding = true;
        Content = root;
        At(root, stage);
        At(root, Switcher(), 1);
        Draw();
        clock.Tick += (_, _) => Advance();
        captureTimer.Tick += CaptureTick;
        PreviewKeyDown += (_, e) =>
        {
            if (concept == 0 && libraryTab != LibraryTab.Scores) return;
            if (e.Key == Key.F8)
            {
                if (!active) Play();
                e.Handled = true;
            }
            if (e.Key == Key.F9)
            {
                Play();
                e.Handled = true;
            }
            if (e.Key == Key.F10)
            {
                Stop(true);
                e.Handled = true;
            }
        };
        Closed += (_, _) =>
        {
            clock.Stop();
            captureTimer.Stop();
            audio.Dispose();
        };
    }
    private Border Switcher()
    {
        var nav = Row();
        nav.HorizontalAlignment = HorizontalAlignment.Center;
        for (int i = 0; i < Concepts.Length; i++)
        {
            int n = i;
            var label = Text(Concepts[i], 13, i == concept ? "#FAFCF6" : "#38433E", i == concept);
            nav.Children.Add(Space(Button(Concepts[i], label, () => ShowConcept(n), i == concept ? "#31443C" : "#EFF1EB", 16, new(15, 6, 15, 6)), 3, 0, 3));
        }
        return Box(nav, "#F9FAF7", 0, new(0, 8, 0, 8), "#DCDFD7");
    }
    public void ShowConcept(int index)
    {
        if (index < 0 || index >= Concepts.Length) return;
        if (mini) Compact();
        if (index != concept) CancelCapture();
        Stop(false);
        concept = index;
        root.Children.RemoveAt(1);
        At(root, Switcher(), 1);
        Draw();
    }
    private void Draw()
    {
        settingsTarget = null;
        roll = null;
        elapsed = null;
        playGlyph = null;
        scrub = null;
        status = null;
        listenLabel = null;
        keycaps.Clear();
        Background = root.Background = stage.Background = Brush(Paper);
        stage.Content = mini ? MiniView() : concept switch { 0 => LibraryView(), 1 => StudioView(), _ => RoomView() };
        UpdateIndicators();
    }
    private void Choose(int i)
    {
        Stop(false);
        State.Index = (i + pieces.Count) % pieces.Count;
        State.Position = 0;
        Draw();
    }
    private void ToggleVersion(bool original)
    {
        if (State.Original == original) return;
        Stop(false);
        State.Original = original;
        Draw();
    }
    private void SetPosition(double value)
    {
        if (updating) return;
        Stop(false);
        State.Position = Math.Clamp(value, 0, Current.Duration);
        UpdateIndicators();
    }
    private void Play()
    {
        if (active)
        {
            Stop(false);
            return;
        }
        if (State.Position >= Current.Duration - .05) State.Position = 0;
        Begin(Current.Duration, State.Speed, false);
    }
    private void Listen()
    {
        if (listening)
        {
            Stop(false);
            return;
        }
        Stop(false);
        if (State.Position >= Current.Duration - .1) State.Position = 0;
        double length = Math.Min(8, Current.Duration - State.Position);
        try
        {
            audio.Play(Current, State.Position, length, State.Original);
            Begin(State.Position + length, 1, true);
        }
        catch (Exception error)
        {
            Stop(false);
            if (status != null) status.Text = "音频设备不可用";
            MessageBox.Show(this, error.Message, "试听", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
    private void Begin(double limit, double speed, bool withAudio)
    {
        active = true;
        listening = withAudio;
        end = limit;
        runSpeed = speed;
        basePosition = State.Position;
        stopwatch.Restart();
        clock.Start();
        UpdateIndicators();
    }
    private void Stop(bool rewind)
    {
        if (active) State.Position = Math.Min(end, basePosition + stopwatch.Elapsed.TotalSeconds * runSpeed);
        active = listening = false;
        stopwatch.Reset();
        clock.Stop();
        audio.Stop();
        if (rewind) State.Position = 0;
        UpdateIndicators();
    }
    private void Advance()
    {
        if (!active) return;
        State.Position = Math.Min(end, basePosition + stopwatch.Elapsed.TotalSeconds * runSpeed);
        if (State.Position >= end)
        {
            Stop(false);
            return;
        }
        UpdateIndicators();
    }
    private void UpdateIndicators()
    {
        updating = true;
        try
        {
            if (elapsed != null) elapsed.Text = Piece.Clock(State.Position);
            if (scrub != null) scrub.Value = State.Position;
            if (playGlyph != null) playGlyph.Text = active ? "\uE769" : "\uE768";
            if (status != null) status.Text = listening ? "正在试听" : active ? "正在预览" : Feedback;
            if (listenLabel != null) listenLabel.Text = listening ? "结束试听" : "试听";
            if (roll != null)
            {
                roll.Position = State.Position;
                roll.InvalidateVisual();
            }
            for (int i = 0; i < keycaps.Count; i++) keycaps[i].Background = Brush(active && Current.Hits.Any(n => n.Lane == i && n.Start <= State.Position && n.Start + n.Seconds > State.Position) ? "#41715D" : "#29323B");
        }
        finally
        {
            updating = false;
        }
    }
    private Button Labeled(string name, string glyph, Action action, string fill, string ink, string? border = null)
        => Button(name, Row(Space(Icon(glyph, 14, ink), 0, 0, 6), Text(name, 13, ink, true)), action, fill, 6, new(12, 9, 12, 9), border);
    private Button Tool(string label, string glyph, Action action, string color, string bg = "Transparent", int size = 32, int radius = 7)
    {
        var b = Button(label, Icon(glyph, 15, color), action, bg, radius, new(0));
        b.Width = b.Height = size;
        return b;
    }
    private Button PlayButton(int size, bool round = true)
    {
        string fg = concept == 1 ? "#183127" : "#FBF9F2";
        playGlyph = Icon(active ? "\uE769" : "\uE768", size * .37, fg);
        var b = Button("播放或暂停无按键预览", playGlyph, Play, Accent, round ? size / 2 : 7, new(0));
        b.Width = b.Height = size;
        return b;
    }
    private Button ListenButton(string ink, string fill, string? border = null)
    {
        listenLabel = Text("试听", 13, ink, true);
        var listen = Button("试听", Row(Space(Icon("\uE7F6", 15, ink), 0, 0, 7), listenLabel), Listen, fill, 6, new(13, 8, 13, 8), border);
        listen.SetBinding(AutomationProperties.NameProperty, new System.Windows.Data.Binding(nameof(TextBlock.Text)) { Source = listenLabel });
        listen.ToolTip = "试听当前版本，最长 8 秒";
        return listen;
    }
    private Slider Scrubber()
    {
        scrub = new Slider
        {
            Minimum = 0,
            Maximum = Current.Duration,
            Value = State.Position,
            Foreground = Brush(Accent),
            Background = Brush(Stroke),
            VerticalAlignment = VerticalAlignment.Center,
            SmallChange = .5,
            LargeChange = 2
        };
        AutomationProperties.SetName(scrub, "预览进度");
        scrub.ValueChanged += (_, e) => SetPosition(e.NewValue);
        return scrub;
    }
    private TextBlock TimeLabel()
    {
        return elapsed = Text(Piece.Clock(State.Position), 12, Sub, false, new FontFamily("Consolas"));
    }
    private Grid Footer()
    {
        var grid = GridOf(true, "auto", "20", "auto", "15", "auto", "10", "*", "10", "auto", "20", "auto");
        var controls = Row(Tool("上一首", "\uE892", () => Choose(State.Index - 1), Ink), Space(PlayButton(39, round: false), 5, 0, 5),
            Tool("下一首", "\uE893", () => Choose(State.Index + 1), Ink));
        At(grid, controls);
        At(grid, Button("停止并回到开头", Text("■", 14, Sub), () => Stop(true)), 0, 2);

        At(grid, TimeLabel(), 0, 4);
        At(grid, Scrubber(), 0, 6);
        At(grid, Text(Piece.Clock(Current.Duration), 12, Sub, false, new FontFamily("Consolas")), 0, 8);
        status = Text(Feedback, 12, Sub);
        At(grid, Row(Space(Icon("\uE765", 14, Sub), 0, 0, 6), status), 0, 10);
        grid.Margin = new(22, 7, 22, 7);
        return grid;
    }
    private StackPanel Versions()
    {
        var group = Row();
        var (surface, selected, selectedInk) = concept switch
        {
            0 => ("#DFE7D9", Accent, "#FAFCF4"),
            1 => ("#28362D", "#A7D6C2", "#182821"),
            _ => ("#F0E4DE", "#714451", "#FFFFFF")
        };
        foreach (var (name, original) in new[] { ("九键编配", false), ("原谱", true) })
        {
            bool on = State.Original == original;
            var button = Button(name, Text(name, 12, on ? selectedInk : Sub, on),
                () => ToggleVersion(original), on ? selected : surface, 5, new(11, 5, 11, 5));
            AutomationProperties.SetItemStatus(button, on ? "已选" : "");
            group.Children.Add(Space(button, 0, 0, 3));
        }
        return group;
    }
    private void ImportText()
    {
        Stop(false);
        var layout = Stack();
        layout.Margin = new(24);
        var title = new TextBox { Text = "我的练习曲", Padding = new(8), FontSize = 15, MaxLength = 38 };
        var notes = new TextBox { Text = "1 2 3 5 3 2 1", Padding = new(8), FontSize = 16, MinHeight = 64, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
        var error = Text("", 12, "#963C30");
        error.TextWrapping = TextWrapping.Wrap;
        var dialog = new Window
        {
            Title = "添加简谱",
            Content = layout,
            Width = 415,
            SizeToContent = SizeToContent.Height,
            Owner = this,
            Background = Brush("#F8F8F1"),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize
        };
        layout.Children.Add(Space(Text("添加简谱", 24, "#263C30", true), 0, 0, 0, 13));
        layout.Children.Add(Space(Text("曲名", 13, "#566559"), 0, 0, 0, 4));
        layout.Children.Add(title);
        layout.Children.Add(Space(Text("音符（1–7，空格分隔；0 休止）", 13, "#566559"), 0, 12, 0, 4));
        layout.Children.Add(notes);
        layout.Children.Add(Space(error, 0, 4, 0, 4));
        layout.Children.Add(Labeled("加入示例谱库", "\uE710", () =>
        {
            try
            {
                var p = Piece.FromText(title.Text, notes.Text);
                pieces.Add(p);
                dialog.DialogResult = true;
                Choose(pieces.Count - 1);
            }
            catch (FormatException ex)
            {
                error.Text = ex.Message;
            }
        }, "#315A45", "#FFFFFF"));
        dialog.ShowDialog();
    }
}
