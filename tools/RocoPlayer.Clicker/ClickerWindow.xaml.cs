using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using RocoPlayer.Music;
using RocoPlayer.Playback;

namespace RocoPlayer.Clicker;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var singleton = new Mutex(true, @"Local\RocoPlayer.Clicker", out bool first);
        if (!first) { MessageBox.Show("连点器已在运行。"); return; }
        new Application().Run(new ClickerWindow());
    }
}

public partial class ClickerWindow : Window
{
    private readonly ClickLoop loop = new();
    private readonly DispatcherTimer polling = new() { Interval = TimeSpan.FromMilliseconds(15) };
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? run;
    private Task worker = Task.CompletedTask;
    private TargetWindow? target;
    private Key hotkey = Key.F8, actionKey = Key.Space;
    private char? character = ' ';
    private bool? bindingHotkey;
    private bool registered, held, capturing, closing, allowClose, playerOpen;
    private nint handle;

    public ClickerWindow()
    {
        InitializeComponent();
        ActionBox.ItemsSource = new[] { "鼠标左键", "键盘按键" };
        ModeBox.ItemsSource = new[] { "Win32 兼容", "DD 驱动", "DD 驱动（前台目标）", "窗口消息" };
        ActionBox.SelectedIndex = ModeBox.SelectedIndex = 0;
        SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(HandleMessage);
            Register();
        };
        polling.Tick += (_, _) =>
        {
            bool down = GetAsyncKeyState(KeyInterop.VirtualKeyFromKey(hotkey)) < 0;
            if (down && !held && bindingHotkey == null && !capturing && !closing && !playerOpen) Toggle();
            held = down;
        };
        PreviewKeyDown += OnKey;
        Closing += OnClosing;
        Closed += (_, _) => { Unregister(); lifetime.Dispose(); };
        RefreshUi();
    }

    private void Register()
    {
        Unregister();
        held = GetAsyncKeyState(KeyInterop.VirtualKeyFromKey(hotkey)) < 0;
        registered = RegisterHotKey(handle, 1, 0x4000, (uint)KeyInterop.VirtualKeyFromKey(hotkey));
        if (!registered) polling.Start();
    }
    private void Unregister()
    {
        polling.Stop();
        if (registered) UnregisterHotKey(handle, 1);
        registered = false;
    }
    private nint HandleMessage(nint hwnd, int msg, nint w, nint l, ref bool handled)
    {
        if (msg == 0x312 && w == 1 && registered && bindingHotkey == null && !playerOpen && !capturing && !closing)
        {
            handled = true;
            Toggle();
        }
        return 0;
    }

    private void RefreshUi()
    {
        Fields.IsEnabled = run == null && !loop.NeedsRelease && !capturing && !closing;
        KeyButton.IsEnabled = ActionBox.SelectedIndex == 1;
        CaptureButton.IsEnabled = ActionBox.SelectedIndex == 1 && ModeBox.SelectedIndex >= 2;
        PlayerButton.IsEnabled = !closing && !capturing;
        ToggleButton.IsEnabled = !closing && !capturing && bindingHotkey == null;
        ToggleButton.Content = run != null || loop.NeedsRelease ? $"停止并释放 ({hotkey})" : $"开始运行 ({hotkey})";
        HotkeyButton.Content = bindingHotkey == true ? "按下新的热键…" : hotkey.ToString();
        KeyButton.Content = bindingHotkey == false ? "按下目标键…" : actionKey.ToString();
        CaptureButton.Content = target?.Title ?? "捕获前台窗口";
    }
    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || ModeBox.SelectedIndex < 0 || ActionBox.SelectedIndex < 0) return;
        RefreshUi();
        Notice.Text = ModeBox.SelectedIndex switch
        {
            0 => "Win32 向当前前台程序发送输入。",
            1 => "DD 向当前前台程序发送输入。",
            2 => "DD 仅在捕获的窗口位于前台时发送。",
            _ => "窗口消息将按键投递至捕获的窗口。部分游戏仅接收原始输入。"
        };
    }
    private void BindHotkey(object sender, RoutedEventArgs e) => BeginBinding(true);
    private void BindAction(object sender, RoutedEventArgs e) => BeginBinding(false);
    private void BeginBinding(bool bindHotkey)
    {
        Unregister();
        bindingHotkey = bindHotkey;
        RefreshUi();
    }
    private void OnKey(object sender, KeyEventArgs e)
    {
        if (bindingHotkey == null) return;
        e.Handled = true;
        if (e.IsRepeat) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk is <= 0 or >= 255) return;
        if (bindingHotkey == true) hotkey = key;
        else
        {
            actionKey = key;
            // Preserve the typed character for WM_CHAR without modifying the keyboard's dead-key state.
            var state = new byte[256];
            var value = new System.Text.StringBuilder(8);
            character = GetKeyboardState(state) && ToUnicode((uint)vk, MapVirtualKeyW((uint)vk, 0), state, value, value.Capacity, 4) == 1 && !char.IsControl(value[0])
                ? value[0] : null;
        }
        bindingHotkey = null;
        Register();
        RefreshUi();
    }
    private async void CaptureWindow(object sender, RoutedEventArgs e)
    {
        capturing = true;
        bindingHotkey = null;
        Register();
        RefreshUi();
        Notice.Text = "请在 3 秒内切到目标窗口…";
        try
        {
            await Task.Delay(3000, lifetime.Token);
            target = TargetWindow.Capture();
            Notice.Text = $"已捕获 {target.Title}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Notice.Text = ex.Message; }
        finally { capturing = false; RefreshUi(); }
    }

    private void ToggleClick(object sender, RoutedEventArgs e) => Toggle();
    private async void Toggle()
    {
        if (closing || capturing || playerOpen || bindingHotkey != null) return;
        try
        {
            if (run != null) { run.Cancel(); return; }
            if (loop.NeedsRelease) { loop.RetryRelease(); Notice.Text = "按键已释放"; return; }
            if (!int.TryParse(Interval.Text, out int interval)) throw new ArgumentException("请输入有效间隔");
            var options = new ClickOptions(interval, ActionBox.SelectedIndex == 0, KeyInterop.VirtualKeyFromKey(actionKey),
                character, (SendMode)ModeBox.SelectedIndex, target);
            options.Validate(KeyInterop.VirtualKeyFromKey(hotkey));
            int hold = options.Mode is SendMode.DD or SendMode.DDTarget
                ? int.TryParse(Environment.GetEnvironmentVariable("DD_HOLD_MS"), out int configured) ? Math.Max(1, configured) : 20 : 0;
            using var source = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            run = source;
            RefreshUi();
            Notice.Text = $"正在运行，按 {hotkey} 停止";
            try
            {
                worker = Task.Run(() =>
                {
                    var send = options.Prepare(source.Token);
                    source.Token.ThrowIfCancellationRequested();
                    loop.Run(send, () => options.CanSend() && (options.Mode == SendMode.WindowMessage ||
                        !OverOwnWindow(options.Mouse)), interval, hold, source.Token);
                });
                await worker;
                Notice.Text = "已停止";
            }
            finally { run = null; }
        }
        catch (OperationCanceledException) { Notice.Text = "已停止"; }
        catch (Exception ex) { Notice.Text = ex.Message; }
        finally { RefreshUi(); }
    }
    private static bool OverOwnWindow(bool mouse)
    {
        nint hwnd = mouse && GetCursorPos(out var cursor) ? WindowFromPoint(cursor) : GetForegroundWindow();
        GetWindowThreadProcessId(hwnd, out var process);
        return process == Environment.ProcessId;
    }

    private async void OpenPlayer(object sender, RoutedEventArgs e)
    {
        if (playerOpen || closing) return;
        playerOpen = true;
        Unregister();
        bindingHotkey = null;
        try
        {
            run?.Cancel();
            try { await worker; } catch (Exception) { /* Retry remaining releases after the failed run. */ }
            loop.RetryRelease();
            if (closing) return;
            using var instance = new Mutex(true, @"Local\RocoPlayer.CSharp", out bool first);
            if (!first) { Notice.Text = "乐谱演奏器已在运行"; return; }
            var data = Environment.GetEnvironmentVariable("ROCK_MUSIC_DATA_DIR");
            if (string.IsNullOrWhiteSpace(data)) data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "com.rockmusic.workspace-player");
            var home = Environment.GetEnvironmentVariable("ROCK_MUSIC_HOME") ?? Environment.CurrentDirectory;
            var library = new ScoreLibrary(data, Path.Combine(home, "music_settings.json"));
            new RocoPlayer.MainWindow(library, data) { Owner = this }.ShowDialog();
        }
        catch (Exception ex) { Notice.Text = ex.Message; }
        finally { playerOpen = false; if (!closing) Register(); RefreshUi(); }
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose) return;
        e.Cancel = true;
        if (closing) return;
        closing = true;
        Unregister();
        lifetime.Cancel();
        try
        {
            try { await worker; } catch (Exception) { /* Retry any outstanding release below. */ }
            loop.RetryRelease();
            allowClose = true;
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
        catch (Exception ex) { closing = false; Notice.Text = ex.Message; RefreshUi(); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] private static extern bool GetKeyboardState([Out] byte[] state);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ToUnicode(uint vk, uint scan, byte[] state, System.Text.StringBuilder text, int size, uint flags);
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyW(uint vk, uint type);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint process);
}
