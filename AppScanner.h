#pragma once
#include "pch.h"
#include <winrt/Windows.Graphics.Imaging.h>

namespace wincast::scanner
{
    struct AppItem
    {
        std::wstring Name;
        std::wstring Path;       // Executable path (Win32) or package path (UWP)
        std::wstring AUMID;      // App User Model ID (UWP)
        bool IsUWP = false;
        winrt::Windows::Graphics::Imaging::SoftwareBitmap Icon{ nullptr };
    };

    class AppScanner
    {
    public:
        // Scans the Start Menu and UWP packages to retrieve all launchable applications
        static std::vector<AppItem> ScanApps();

        // Launches the specified application
        static bool Launch(AppItem const& item);

    private:
        // Recursively parses a folder for shortcuts (.lnk)
        static void ScanStartMenuFolder(std::wstring const& folderPath, std::vector<AppItem>& apps);

        // Resolves a .lnk shortcut file to an AppItem
        static bool ParseShortcut(std::wstring const& lnkPath, AppItem& appItem);

        // Helper to convert HICON to WinRT SoftwareBitmap
        static winrt::Windows::Graphics::Imaging::SoftwareBitmap GetBitmapFromHIcon(HICON hIcon);
        
        // Helper to extract UWP app icon
        static winrt::Windows::Graphics::Imaging::SoftwareBitmap GetUwpAppIcon(winrt::Windows::ApplicationModel::Core::AppListEntry const& entry);
    };
}
