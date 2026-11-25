using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RocoPlayer;

public partial class MainWindow
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PixelRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public PixelRect Monitor, Work; public uint Flags; }
    [DllImport("user32")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    private Rect CurrentWorkArea()
    {
        var source = (HwndSource?)PresentationSource.FromVisual(this);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (source?.CompositionTarget == null || !GetMonitorInfo(MonitorFromWindow(source.Handle, 2), ref info))
            return SystemParameters.WorkArea;
        var transform = source.CompositionTarget.TransformFromDevice;
        return new Rect(transform.Transform(new Point(info.Work.Left, info.Work.Top)),
            transform.Transform(new Point(info.Work.Right, info.Work.Bottom)));
    }
}
