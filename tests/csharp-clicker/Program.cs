using System.IO;
using System.Windows.Controls;
using RocoPlayer.Clicker;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Construct the real layout before showing the HWND. No global hotkey or input APIs run here.
        var window = new ClickerWindow();
        Check(((TextBox)window.FindName("Interval")).Text == "10", "默认间隔");
        Check(((ComboBox)window.FindName("ModeBox")).SelectedIndex == 0, "默认 Win32");
        Check(((Button)window.FindName("ToggleButton")).Content.ToString() == "开始运行 (F8)", "启动后应保持空闲");
        var mouse = new ClickOptions(10, true, 32, null, SendMode.Win32, null);
        mouse.Validate(119);
        ExpectInvalid(() => (mouse with { IntervalMs = -1 }).Validate(119));
        ExpectInvalid(() => (mouse with { Mouse = false, Vk = 119 }).Validate(119));
        ExpectInvalid(() => (mouse with { Mode = SendMode.DDTarget }).Validate(119));
        ExpectInvalid(() => (mouse with { Mouse = false, Mode = SendMode.WindowMessage }).Validate(119));

        var loop = new ClickLoop();
        var events = new List<bool>();
        using (var cancel = new CancellationTokenSource())
        {
            int checks = 0;
            loop.Run(down => { events.Add(down); if (down) cancel.Cancel(); }, () => ++checks > 2, 1, 2000, cancel.Token);
            Check(checks == 3 && events.SequenceEqual([true, false]), "失焦跳过，停止时及时释放");
        }

        bool fail = true;
        try
        {
            loop.Run(down => { if (!down && fail) throw new IOException("模拟释放失败"); }, () => true, 1, 0, default);
            throw new Exception("应报告释放失败");
        }
        catch (IOException) { }
        Check(loop.NeedsRelease, "保留未释放输入");
        fail = false;
        loop.RetryRelease();
        Check(!loop.NeedsRelease, "重试释放");
        Console.WriteLine("通过：连点器初始设置、热键冲突、目标窗口要求、失焦与取消、释放失败重试。");
    }
    private static void ExpectInvalid(Action action)
    {
        try { action(); } catch (ArgumentException) { return; }
        throw new Exception("未拒绝无效设置");
    }
    private static void Check(bool result, string reason) { if (!result) throw new Exception(reason); }
}
