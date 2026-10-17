using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace NovaVpn;

internal static class WindowsBackdrop
{
    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left,Right,Top,Bottom; }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window,int attribute,ref int value,int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr window,ref Margins margins);
    public static bool Apply(Window window, bool dark, bool disabled)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;
        try {
            int mode = dark ? 1 : 0; DwmSetWindowAttribute(hwnd,20,ref mode,4);
            int corners = 2;DwmSetWindowAttribute(hwnd,33,ref corners,4);
            bool transparency = true;
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                transparency = Convert.ToInt32(key?.GetValue("EnableTransparency",1) ?? 1) != 0;
            bool enabled = !disabled && transparency && !SystemParameters.HighContrast && SystemParameters.PowerLineStatus != PowerLineStatus.Offline;
            int material = enabled ? 2 : 1;
            bool supported = DwmSetWindowAttribute(hwnd,38,ref material,4) == 0;
            var margins = new Margins { Left = enabled && supported ? -1 : 0 };
            DwmExtendFrameIntoClientArea(hwnd,ref margins);
            if (enabled && supported) {
                var source = HwndSource.FromHwnd(hwnd);
                if (source?.CompositionTarget != null) source.CompositionTarget.BackgroundColor = Colors.Transparent;
            }
            return enabled && supported;
        } catch (DllNotFoundException) { return false; }
          catch (EntryPointNotFoundException) { return false; }
    }
}
