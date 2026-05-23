#include "pch.h"
#include "App.xaml.h"
#include <MddBootstrap.h>
#include <WindowsAppSDK-VersionInfo.h>

using namespace winrt;

int __stdcall wWinMain(HINSTANCE /*hInstance*/, HINSTANCE /*hPrevInstance*/, LPWSTR /*lpCmdLine*/, int /*nCmdShow*/)
{
    // Single instance check via Mutex
    HANDLE hMutex = CreateMutexW(NULL, TRUE, L"Local\\WinCastSingleInstanceMutex");
    if (hMutex == NULL || GetLastError() == ERROR_ALREADY_EXISTS)
    {
        if (hMutex) CloseHandle(hMutex);
        
        // Find existing WinCast window and activate it if it exists
        HWND hWndExisting = FindWindowW(NULL, L"WinCast");
        if (hWndExisting)
        {
            // Send user message to show the window
            PostMessageW(hWndExisting, WM_USER + 1, 0, 0);
        }
        return 0;
    }

    // Initialize COM
    winrt::init_apartment(winrt::apartment_type::single_threaded);

    // Initialize Bootstrapper
    // Use the release version constants from WindowsAppSDK-VersionInfo.h
    const HRESULT hr = MddBootstrapInitialize2(
        ::Microsoft::WindowsAppSDK::Release::MajorMinor,
        ::Microsoft::WindowsAppSDK::Release::VersionTag,
        PACKAGE_VERSION{ 0 },
        MddBootstrapInitializeOptions_OnError_DebugBreak
    );

    if (FAILED(hr))
    {
        MessageBoxW(NULL, L"Failed to initialize Windows App SDK runtime.\nPlease install the Windows App SDK runtime.", L"WinCast Initialization Error", MB_OK | MB_ICONERROR);
        CloseHandle(hMutex);
        return hr;
    }

    // Start the application
    winrt::Microsoft::UI::Xaml::Application::Start([](auto&&) {
        winrt::make<winrt::wincast::implementation::App>();
    });

    // Shutdown Bootstrapper
    MddBootstrapShutdown();

    CloseHandle(hMutex);
    return 0;
}
