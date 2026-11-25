using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using static RocoPlayer.Prototypes.Ui;

namespace RocoPlayer.Prototypes;

public sealed partial class PrototypeWindow
{
    private UIElement LocalLibrary()
    {
        var columns = GridOf(true, "1.18*", "*");
        var library = GridOf(false, "auto", "auto", "auto", "*");
        library.Margin = new(28, 26, 28, 20);
        var heading = GridOf(true, "*", "auto");
        At(heading, Text("本地谱库", 30, Ink, true));
        var add = Labeled("添加简谱", "\uE710", ImportText, Accent, "#FAFCF4");
        add.VerticalAlignment = VerticalAlignment.Center;
        At(heading, add, 0, 1);
        At(library, heading);

        var rows = Stack();
        void ShowRows()
        {
            rows.Children.Clear();
            foreach (int index in LibraryOrder())
                rows.Children.Add(LibraryRow(index));
        }
        var tools = GridOf(true, "*", "auto");
        tools.Margin = new(0, 14, 0, 13);
        At(tools, Text($"{pieces.Count} 首曲谱", 13, Sub));
        var sort = Choice("曲谱排序", ["下载顺序", "曲谱名称"], sortByName ? 1 : 0, i =>
        {
            sortByName = i == 1;
            ShowRows();
        });
        At(tools, Row(Space(Text("排序", 12, Sub), 0, 0, 7), sort), 0, 1);
        At(library, tools, 1);
        var labels = GridOf(true, "*", "auto");
        labels.Margin = new(12, 0, 12, 8);
        At(labels, Text("曲名", 12, Sub));
        At(labels, Text("时长", 12, Sub), 0, 1);
        At(library, labels, 2);
        ShowRows();
        var list = new ScrollViewer
        {
            Content = rows,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        AutomationProperties.SetName(list, "曲谱列表");
        At(library, list, 3);
        At(columns, library);

        var playerPane = GridOf(false, "*", "auto");
        At(playerPane, CurrentTrackPanel(showStop: true));
        At(playerPane, new Border
        {
            Child = ScoreDetails(), BorderBrush = Brush(Stroke),
            BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 13, 0, 0)
        }, 1);
        At(columns, new Border
        {
            Background = Brush("#EDF1E8"),
            BorderBrush = Brush(Stroke),
            BorderThickness = new Thickness(1, 0, 0, 0),
            Padding = new Thickness(29, 12, 29, 22),
            Child = playerPane
        }, 0, 1);
        return columns;
    }

    private Button LibraryRow(int index)
    {
        var piece = pieces[index];
        bool selected = index == State.Index;
        var row = GridOf(true, "12", "*", "8", "auto");
        if (selected)
            At(row, new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = 3,
                Height = 28,
                CornerRadius = new CornerRadius(1.5),
                Background = Brush(Accent)
            });

        var info = Stack(
            Text(piece.Title, 16, Ink, selected),
            Space(Text($"{piece.Source}   ·   {piece.Bpm} BPM", 12, Sub), 0, 2));
        At(row, info, 0, 1);
        At(row, Text(Piece.Clock(piece.Duration), 13, Sub, face: new FontFamily("Consolas")), 0, 3);
        var button = Button("选择 " + piece.Title, row, () => Choose(index),
            selected ? "#E1EADB" : "Transparent", 6, new(0, 9, 12, 9));
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        button.Margin = new(0, 0, 0, 4);
        if (selected)
        {
            AutomationProperties.SetItemStatus(button, "当前曲目");
            button.Loaded += (_, _) => button.BringIntoView();
        }
        return button;
    }

    private UIElement StudioView()
    {
        var screen = GridOf(false, "60", "*", "60");
        var top = GridOf(true, "auto", "18", "*", "auto");
        top.Margin = new(22, 0, 22, 0);
        At(top, Brand("#DBF1DC"));
        At(top, Text("演奏台", 14, Sub), 0, 2);
        At(top, Row(Space(Icon("\uE765", 15, "#ACD8B2"), 0, 0, 6), Text("无按键预览", 12, "#C1D9C4")), 0, 3);
        At(screen, Box(top, "#212831", 0, null, Stroke));
        var body = GridOf(true, "*", "210");
        At(screen, body, 1);
        var main = GridOf(false, "74", "34", "*", "41", "66");
        main.Margin = new(22, 9, 22, 17);
        var title = Stack(Text(Current.Title, 30, Ink, true), Space(Text($"{Current.Source}   /   {Current.Bpm} BPM   /   {Current.Hits.Length} 个音符", 12, Sub), 0, 3));
        title.VerticalAlignment = VerticalAlignment.Center;
        At(main, title);
        var tools = GridOf(true, "*", "auto");
        At(tools, Text("音符时间线", 15, Ink, true));
        At(tools, Versions(), 0, 1);
        At(main, tools, 1);
        roll = new NoteRoll(Current, State.Original, SetPosition) { Margin = new(5, 8, 2, 5), Cursor = System.Windows.Input.Cursors.Cross };
        AutomationProperties.SetName(roll, "当前四小节，点击可改变预览位置");
        At(main, Box(roll, "#171D24", 6, new(7), Stroke), 2);
        At(main, Text("九键映射", 14, Ink, true), 3);
        At(main, Keybed(), 4);
        At(body, main);
        var side = GridOf(false, "40", "*", "auto", "33", "38");
        side.Margin = new(15, 12, 15, 17);
        var head = GridOf(true, "*", "auto");
        At(head, Text("演奏队列", 16, Ink, true));
        At(head, Text($"{pieces.Count} 首", 12, Sub), 0, 1);
        At(side, head);
        var songs = Stack();
        foreach (var piece in pieces)
        {
            int i = pieces.IndexOf(piece);
            var row = Row(Art(piece, 33), Space(Stack(Text(piece.Title, 13, Ink, i == State.Index), Text(Piece.Clock(piece.Duration), 11, Sub)), 8));
            var b = Button("切换到 " + piece.Title, row, () => Choose(i), i == State.Index ? "#33433E" : "Transparent", 6, new(7, 8, 7, 8));
            b.HorizontalContentAlignment = HorizontalAlignment.Left;
            songs.Children.Add(Space(b, 0, 0, 0, 3));
        }
        At(side, new ScrollViewer { Content = songs, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, 1);
        var speed = GridOf(true, "*", "auto");
        At(speed, Text("预览速度", 13, Sub));
        At(speed, Button("切换预览速度",
                   Text($"{State.Speed:0.##}×  ⌄", 13, Ink, true), () =>
                   {
                       Stop(false);
                       double[] steps = [.75, 1, 1.25, 1.5];
                       State.Speed = steps[(Array.IndexOf(steps, State.Speed) + 1) % steps.Length];
                       Draw();
                   }, "#303943", 4, new(8, 4, 8, 4)), 0, 1);
        At(side, speed, 2);
        At(side, Text("试听当前版本 · 8 秒", 12, Sub), 3);
        At(side, ListenButton("#D4E5D4", "#303A45", Stroke), 4);
        At(body, Box(side, "#242B33", 0, null, Stroke), 0, 1);
        At(screen, Box(Footer(), "#212831", 0, null, Stroke), 2);
        return screen;
    }
    private UIElement Keybed()
    {
        var row = GridOf(true, Enumerable.Repeat("*", 9).ToArray());
        for (int i = 0; i < 9; i++)
        {
            var key = Text(Piece.Keys[i], 18, "#F4F6EA", true, new FontFamily("Consolas"));
            key.HorizontalAlignment = HorizontalAlignment.Center;
            var note = Text(Piece.PitchNames[i], 11, "#A9BDAE");
            note.HorizontalAlignment = HorizontalAlignment.Center;
            var pair = Stack(key, note);
            pair.VerticalAlignment = VerticalAlignment.Center;
            var cap = Box(pair, "#29323B", 5, new(4), Stroke);
            keycaps.Add(cap);
            At(row, Space(cap, i == 0 ? 0 : 4), 0, i);
        }
        return row;
    }
    private UIElement RoomView()
    {
        var screen = GridOf(false, "61", "*", "128");
        screen.Margin = new(26, 0, 26, 14);
        var top = GridOf(true, "auto", "16", "*", "auto");
        At(top, Brand(Ink));
        At(top, Text("听室", 14, Sub), 0, 2);
        var miniButton = Labeled("小窗", "\uE8A7", Compact, "Transparent", Ink, Stroke);
        miniButton.VerticalAlignment = VerticalAlignment.Center;
        At(top, miniButton, 0, 3);
        At(screen, top);
        var main = GridOf(true, "1.1*", "*");
        At(screen, main, 1);
        var sleeve = new RecordSleeve(Current) { Margin = new(6, 16, 12, 16) };
        At(main, sleeve);
        At(main, Space(CurrentTrackPanel(), 25, 6, 10, 6), 0, 1);
        var shelf = GridOf(false, "35", "*");
        At(shelf, Text("曲目架", 15, Ink, true));
        var stack = new System.Windows.Controls.Primitives.UniformGrid { Columns = 4 };
        foreach (var song in pieces)
        {
            int i = pieces.IndexOf(song);
            var content = GridOf(true, "45", "*");
            At(content, Art(song, 45));
            At(content, Space(Stack(Text(song.Title, 14, Ink, true), Space(Text(song.Source + " · " + Piece.Clock(song.Duration), 12, Sub), 0, 3)), 8), 0, 1);
            var b = Button("选择 " + song.Title, content, () => Choose(i), i == State.Index ? "#E8DCD3" : "#F2E9E2", 6, new(8), i == State.Index ? "#B89789" : Stroke);
            b.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            stack.Children.Add(Space(b, 0, 0, 7, 5));
        }
        At(shelf, new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, 1);
        At(screen, shelf, 2);
        return screen;
    }
    private StackPanel CurrentTrackPanel(bool showStop = false)
    {
        var panel = Stack();
        panel.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(panel, "当前曲目");
        panel.Children.Add(Text("当前曲目", 13, Sub));
        var title = Text(Current.Title, 43, Ink, true, Serif);
        title.TextWrapping = TextWrapping.Wrap;
        title.MaxHeight = 114;
        title.ToolTip = Current.Title;
        panel.Children.Add(Space(title, 0, 3, 0, 4));
        string metadata = $"{Current.Source}   ·   {Current.Bpm} BPM";
        if (!showStop) metadata += $"   ·   {Current.Hits.Length} 个音符";
        panel.Children.Add(Text(metadata, 13, Sub));
        panel.Children.Add(Space(Scrubber(), 0, 17));
        var times = GridOf(true, "*", "auto");
        At(times, TimeLabel());
        At(times, Text(Piece.Clock(Current.Duration), 12, Sub, face: new FontFamily("Consolas")), 0, 1);
        panel.Children.Add(times);

        var controls = Row(
            Tool("上一首", "\uE892", () => Neighbor(-1), Ink),
            Space(PlayButton(50), 8, 0, 8),
            Tool("下一首", "\uE893", () => Neighbor(1), Ink));
        if (showStop)
            controls.Children.Add(Tool("停止并回到开头", "\uE71A", () => Stop(true), Sub));
        controls.Children.Add(Space(ListenButton(Accent, "Transparent", Stroke), 12));
        panel.Children.Add(Space(controls, 0, 14, 0, 12));
        status = Text(Feedback, 12, Sub);
        panel.Children.Add(Row(Versions(), Space(status, 9)));
        return panel;
    }

    private UIElement MiniView()
    {
        var g = GridOf(true, "50", "*", "auto", "auto", "auto", "auto");
        g.Margin = new(12, 6, 10, 6);
        At(g, Art(Current, 45));
        At(g, Space(Stack(Text(Current.Title, 15, Ink, true), TimeLabel()), 8), 0, 1);
        At(g, Tool("上一首", "\uE892", () => Choose(State.Index - 1), Ink), 0, 2);
        At(g, PlayButton(37), 0, 3);
        At(g, Tool("下一首", "\uE893", () => Choose(State.Index + 1), Ink), 0, 4);
        At(g, Tool("展开听室", "\uE740", Compact, Ink), 0, 5);
        return g;
    }
    private void Compact()
    {
        mini = !mini;
        if (mini)
        {
            expandedState = WindowState;
            expandedBounds = WindowState == WindowState.Normal ? new(Left, Top, Width, Height) : RestoreBounds;
            WindowState = WindowState.Normal;
            MinWidth = 350;
            MinHeight = 100;
            Width = 420;
            Height = 108;
            root.RowDefinitions[1].Height = new GridLength(0);
            root.Children[1].Visibility = Visibility.Collapsed;
        }
        else
        {
            MinWidth = 868;
            MinHeight = 680;
            Left = expandedBounds.Left;
            Top = expandedBounds.Top;
            Width = expandedBounds.Width;
            Height = expandedBounds.Height;
            WindowState = expandedState;
            root.RowDefinitions[1].Height = new GridLength(55);
            root.Children[1].Visibility = Visibility.Visible;
        }
        Topmost = mini;
        Draw();
    }
}
