#pragma once
#include "pch.h"

namespace wincast::utils
{
    class WindowHelper
    {
    public:
        // Returns the HWND of a WinUI 3 Window
        static HWND GetHWND(winrt::Microsoft::UI::Xaml::Window const& window);

        // Sets custom borderless styles, shadow, and removes titlebar
        static void MakeBorderless(HWND hwnd);

        // Centers the window on the active monitor with specific width/height
        static void CenterWindow(HWND hwnd, int width, int height);

        // Enables Mica or Acrylic system backdrop on the window
        static void ApplyBackdrop(winrt::Microsoft::UI::Xaml::Window const& window, bool useAcrylic = false);

        // Forces active window focus and brings to foreground
        static void ForceForeground(HWND hwnd);
    };
}
