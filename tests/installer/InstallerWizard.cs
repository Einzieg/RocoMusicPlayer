using System;
using System.Runtime.InteropServices;
using System.Text;

// Operate the native NSIS controls by handle so the check also works on a hosted runner's hidden desktop.
public static class InstallerWizard
{
    private delegate bool EnumWindow(IntPtr window, IntPtr context);
    [DllImport("user32")] private static extern bool EnumWindows(EnumWindow callback, IntPtr context);
    [DllImport("user32")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindow callback, IntPtr context);
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int process);
    [DllImport("user32")] private static extern IntPtr GetDlgItem(IntPtr window, int id);
    [DllImport("user32")] private static extern int GetDlgCtrlID(IntPtr window);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int size);
    [DllImport("user32")] private static extern IntPtr GetLastActivePopup(IntPtr window);
    [DllImport("user32")] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32")] private static extern int GetWindowLongW(IntPtr window, int index);
    [DllImport("user32")] private static extern bool PostMessageW(IntPtr window, uint message, IntPtr value, IntPtr control);
    [DllImport("user32")] private static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr value, IntPtr data);
    [DllImport("user32", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW", ExactSpelling = true)]
    private static extern IntPtr ReadText(IntPtr window, uint message, int capacity, StringBuilder text);
    [DllImport("user32", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW", ExactSpelling = true)]
    private static extern IntPtr WriteText(IntPtr window, uint message, IntPtr value, string text);

    public static IntPtr MainDialog(int pid)
    {
        IntPtr result = IntPtr.Zero;
        EnumWindows((h, _) => {
            GetWindowThreadProcessId(h, out int owner);
            if (owner != pid || GetDlgItem(h, 3) == IntPtr.Zero) return true;
            result = h;
            return false;
        }, IntPtr.Zero);
        return result;
    }

    private static IntPtr Find(IntPtr dialog, Func<IntPtr, bool> matches)
    {
        IntPtr result = IntPtr.Zero;
        EnumChildWindows(dialog, (h, _) => {
            if (!matches(h)) return true;
            result = h;
            return false;
        }, IntPtr.Zero);
        return result;
    }

    private static string ClassName(IntPtr h)
    {
        var text = new StringBuilder(64);
        GetClassName(h, text, text.Capacity);
        return text.ToString();
    }
    public static string Text(IntPtr h)
    {
        var text = new StringBuilder(1024);
        ReadText(h, 0x000D, text.Capacity, text);
        return text.ToString();
    }
    public static IntPtr DirectoryEdit(IntPtr dialog) => Find(dialog, h => GetDlgCtrlID(h) == 1019 && ClassName(h) == "Edit");
    public static void SetDirectory(IntPtr edit, string path) => WriteText(edit, 0x000C, IntPtr.Zero, path);
    public static IntPtr Checkbox(IntPtr dialog) => Find(dialog, h => ClassName(h) == "Button" && (GetWindowLongW(h, -16) & 15) == 3);
    public static bool ShowsPath(IntPtr dialog, string path) => Find(dialog, h => ClassName(h) == "Static" && Text(h) == path) != IntPtr.Zero;
    public static bool IsChecked(IntPtr box) => SendMessageW(box, 0x00F0, IntPtr.Zero, IntPtr.Zero) != IntPtr.Zero;
    public static void Check(IntPtr box) => SendMessageW(box, 0x00F1, new IntPtr(1), IntPtr.Zero);
    public static string NextCaption(IntPtr dialog) => Text(GetDlgItem(dialog, 1)).Replace("&", "");
    public static bool HasPage(IntPtr dialog) => Find(dialog, h => ClassName(h) == "#32770") != IntPtr.Zero;
    public static void Next(IntPtr dialog)
    {
        var button = GetDlgItem(dialog, 1);
        if (button != IntPtr.Zero && IsWindowEnabled(button))
            PostMessageW(dialog, 0x0111, new IntPtr(1), button);
    }
    public static string ModalMessage(IntPtr dialog)
    {
        var popup = GetLastActivePopup(dialog);
        if (popup == IntPtr.Zero || popup == dialog || IsWindowEnabled(dialog)) return "";
        var label = Find(popup, h => ClassName(h) == "Static" && Text(h).Length > 0);
        return label == IntPtr.Zero ? "" : Text(label);
    }
    public static void ConfirmModal(IntPtr dialog)
    {
        var popup = GetLastActivePopup(dialog);
        var button = Find(popup, h => ClassName(h) == "Button" && IsWindowEnabled(h));
        if (button != IntPtr.Zero) PostMessageW(popup, 0x0111, new IntPtr(GetDlgCtrlID(button)), button);
    }
}
