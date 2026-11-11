using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace NovaVpn;

internal static class WindowsWindowBounds
{
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public int Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    public static void Attach(Window window)
    {
        HwndSource source = null;
        window.SourceInitialized += delegate {
            source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            source?.AddHook(HandleMessage);
        };
        window.Closed += delegate { source?.RemoveHook(HandleMessage); };
    }

    public static Size WorkAreaSize(Window owner)
    {
        var source = owner == null ? null : PresentationSource.FromVisual(owner) as HwndSource;
        var fallback = SystemParameters.WorkArea.Size;
        if (source?.CompositionTarget == null || !TryGetMonitorInfo(source.Handle, out var info))
            return fallback;

        var pixels = new Vector(info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
        var logical = source.CompositionTarget.TransformFromDevice.Transform(pixels);
        return logical.X > 0 && logical.Y > 0 ? new Size(logical.X, logical.Y) : fallback;
    }

    private static bool TryGetMonitorInfo(IntPtr hwnd, out MonitorInfo info)
    {
        var monitor = MonitorFromWindow(hwnd, 2); // nearest monitor, including secondary screens
        info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
        return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info);
    }

    private static IntPtr HandleMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != 0x0024 || lParam == IntPtr.Zero) return IntPtr.Zero; // WM_GETMINMAXINFO
        if (!TryGetMonitorInfo(hwnd, out var info)) return IntPtr.Zero;
        var limits = (MinMaxInfo)Marshal.PtrToStructure(lParam, typeof(MinMaxInfo));
        limits.MaxPosition.X = info.Work.Left - info.Monitor.Left;
        limits.MaxPosition.Y = info.Work.Top - info.Monitor.Top;
        limits.MaxSize.X = info.Work.Right - info.Work.Left;
        limits.MaxSize.Y = info.Work.Bottom - info.Work.Top;
        Marshal.StructureToPtr(limits, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }
}
