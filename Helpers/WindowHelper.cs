using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace WinCast;

internal static class WindowHelper
{
    internal static IntPtr GetHWND(Window window)
    {
        return WinRT.Interop.WindowNative.GetWindowHandle(window);
    }

    internal static void MakeBorderless(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        // Use WinUI 3 AppWindow to disable the default border and title bar
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
        }

        long style = NativeMethods.GetWindowLongPtrW(hwnd, NativeMethods.GWL_STYLE).ToInt64();
        style &= ~(NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME |
                    NativeMethods.WS_MINIMIZEBOX | NativeMethods.WS_MAXIMIZEBOX | NativeMethods.WS_SYSMENU);
        style |= NativeMethods.WS_POPUP;
        NativeMethods.SetWindowLongPtrW(hwnd, NativeMethods.GWL_STYLE, new IntPtr(style));

        long exStyle = NativeMethods.GetWindowLongPtrW(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLongPtrW(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));

        uint cornerPref = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(uint));

        // Suppress drawing the default flat 1px window border frame
        uint borderColor = NativeMethods.DWMWA_COLOR_NONE;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_BORDER_COLOR, ref borderColor, sizeof(uint));

        // Note: DwmExtendFrameIntoClientArea is not needed here as Mica/Acrylic backdrops manage their own frame extensions.
        // Calling it with 1px margins manually introduces an outer 1px flat native border.
        // var margins = new NativeMethods.MARGINS { cxLeftWidth = 1, cxRightWidth = 1, cxTopHeight = 1, cxBottomHeight = 1 };
        // NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);

        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER |
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
    }

    internal static void CenterWindow(IntPtr hwnd, int width, int height)
    {
        if (hwnd == IntPtr.Zero) return;

        NativeMethods.GetCursorPos(out var cursorPt);
        IntPtr hMonitor = NativeMethods.MonitorFromPoint(cursorPt, NativeMethods.MONITOR_DEFAULTTONEAREST);

        var monitorInfo = new NativeMethods.MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (NativeMethods.GetMonitorInfoW(hMonitor, ref monitorInfo))
        {
            int workWidth = monitorInfo.rcWork.Right - monitorInfo.rcWork.Left;
            int workHeight = monitorInfo.rcWork.Bottom - monitorInfo.rcWork.Top;
            int x = monitorInfo.rcWork.Left + (workWidth - width) / 2;
            int y = monitorInfo.rcWork.Top + (workHeight - height) / 3;
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, x, y, width, height, NativeMethods.SWP_SHOWWINDOW);
            ApplyRoundedRegion(hwnd, width, height);
        }
    }

    private static void ApplyRoundedRegion(IntPtr hwnd, int width, int height)
    {
        if (hwnd == IntPtr.Zero || width <= 0 || height <= 0) return;

        const int radius = 28;
        IntPtr region = NativeMethods.CreateRoundRectRgn(0, 0, width + 1, height + 1, radius, radius);
        if (region == IntPtr.Zero) return;

        if (NativeMethods.SetWindowRgn(hwnd, region, true) == 0)
            NativeMethods.DeleteObject(region);
    }

    internal static void ApplyBackdrop(Window window, bool useAcrylic = false)
    {
        if (useAcrylic)
            window.SystemBackdrop = new DesktopAcrylicBackdrop();
        else
            window.SystemBackdrop = new MicaBackdrop();
    }

    internal static void ForceForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        uint foregroundThread = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out _);
        uint thisThread = (uint)NativeMethods.GetCurrentThreadId();
        if (foregroundThread != thisThread)
            NativeMethods.AttachThreadInput(foregroundThread, thisThread, true);

        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE);
        NativeMethods.SetForegroundWindow(hwnd);
        NativeMethods.SetFocus(hwnd);
        NativeMethods.SetActiveWindow(hwnd);

        if (foregroundThread != thisThread)
            NativeMethods.AttachThreadInput(foregroundThread, thisThread, false);
    }
}
