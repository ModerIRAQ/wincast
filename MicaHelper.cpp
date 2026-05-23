#include "pch.h"
#include "MicaHelper.h"
#include <microsoft.ui.xaml.window.h>

namespace wincast::utils
{
    HWND WindowHelper::GetHWND(winrt::Microsoft::UI::Xaml::Window const& window)
    {
        auto nativeWindow = window.as<IWindowNative>();
        HWND hwnd = nullptr;
        if (nativeWindow)
        {
            nativeWindow->get_WindowHandle(&hwnd);
        }
        return hwnd;
    }

    void WindowHelper::MakeBorderless(HWND hwnd)
    {
        if (!hwnd) return;

        // Get current style
        LONG_PTR style = GetWindowLongPtrW(hwnd, GWL_STYLE);
        
        // Strip out title bar, thick frame, system menu, etc.
        style &= ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU);
        style |= WS_POPUP; // Ensure it behaves as a popup
        SetWindowLongPtrW(hwnd, GWL_STYLE, style);

        // Modify extended style to make it a tool window (hides from Alt-Tab)
        LONG_PTR exStyle = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
        exStyle |= WS_EX_TOOLWINDOW;
        SetWindowLongPtrW(hwnd, GWL_EXSTYLE, exStyle);

        // Apply Windows 11 Rounded Corners (DWM attribute)
        DWM_WINDOW_CORNER_PREFERENCE cornerPreference = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, &cornerPreference, sizeof(cornerPreference));

        // Enable shadow via DWM
        MARGINS margins = { 1, 1, 1, 1 };
        DwmExtendFrameIntoClientArea(hwnd, &margins);

        // Notify shell of style changes
        SetWindowPos(hwnd, nullptr, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    void WindowHelper::CenterWindow(HWND hwnd, int width, int height)
    {
        if (!hwnd) return;

        // Get monitor where the cursor currently is, or fallback to primary monitor
        POINT cursorPt;
        GetCursorPos(&cursorPt);
        HMONITOR hMonitor = MonitorFromPoint(cursorPt, MONITOR_DEFAULTTONEAREST);

        MONITORINFO monitorInfo = { sizeof(MONITORINFO) };
        if (GetMonitorInfoW(hMonitor, &monitorInfo))
        {
            // Work area excludes the taskbar
            int workWidth = monitorInfo.rcWork.right - monitorInfo.rcWork.left;
            int workHeight = monitorInfo.rcWork.bottom - monitorInfo.rcWork.top;

            int x = monitorInfo.rcWork.left + (workWidth - width) / 2;
            // Spotlight is typically placed slightly above vertical center (e.g. 1/3 from the top)
            int y = monitorInfo.rcWork.top + (workHeight - height) / 3;

            SetWindowPos(hwnd, HWND_TOPMOST, x, y, width, height, SWP_SHOWWINDOW);
        }
    }

    void WindowHelper::ApplyBackdrop(winrt::Microsoft::UI::Xaml::Window const& window, bool useAcrylic)
    {
        if (useAcrylic)
        {
            window.SystemBackdrop(winrt::Microsoft::UI::Xaml::Media::DesktopAcrylicBackdrop());
        }
        else
        {
            window.SystemBackdrop(winrt::Microsoft::UI::Xaml::Media::MicaBackdrop());
        }
    }

    void WindowHelper::ForceForeground(HWND hwnd)
    {
        if (!hwnd) return;

        // Bypass Windows foreground lock by attaching thread input
        DWORD foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), nullptr);
        DWORD thisThread = GetCurrentThreadId();
        if (foregroundThread != thisThread)
        {
            AttachThreadInput(foregroundThread, thisThread, TRUE);
        }

        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
        SetForegroundWindow(hwnd);
        SetFocus(hwnd);
        SetActiveWindow(hwnd);

        if (foregroundThread != thisThread)
        {
            AttachThreadInput(foregroundThread, thisThread, FALSE);
        }
    }
}
