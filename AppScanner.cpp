#include "pch.h"
#include "AppScanner.h"
#include <shobjidl.h>
#include <MemoryBuffer.h>

// COM Guid for IMemoryBufferByteAccess
struct __declspec(uuid("5b0d3235-4dba-4d44-865e-8f1d0e4fd04d")) IMemoryBufferByteAccess : ::IUnknown
{
    virtual HRESULT __stdcall GetBuffer(BYTE** value, UINT32* capacity) = 0;
};

// IApplicationActivationManager is already defined in <shobjidl.h>

// Property Key for AUMID in shortcuts
const PROPERTYKEY PKEY_AppUserModel_ID = { { 0x9F4C6855, 0x3779, 0x4F77, { 0x75, 0x1A, 0x88, 0xEC, 0x58, 0x58, 0x99, 0x18 } }, 5 };

namespace wincast::scanner
{
    std::vector<AppItem> AppScanner::ScanApps()
    {
        // Initialize COM on this background thread
        winrt::init_apartment(winrt::apartment_type::multi_threaded);

        std::vector<AppItem> apps;

        // 1. Scan Win32 Start Menu Shortcuts
        // System Start Menu
        WCHAR szPath[MAX_PATH];
        if (SUCCEEDED(SHGetFolderPathW(NULL, CSIDL_COMMON_STARTMENU, NULL, 0, szPath)))
        {
            std::wstring systemStartMenu = std::wstring(szPath) + L"\\Programs";
            ScanStartMenuFolder(systemStartMenu, apps);
        }
        
        // User Start Menu
        if (SUCCEEDED(SHGetFolderPathW(NULL, CSIDL_STARTMENU, NULL, 0, szPath)))
        {
            std::wstring userStartMenu = std::wstring(szPath) + L"\\Programs";
            ScanStartMenuFolder(userStartMenu, apps);
        }

        // 2. Scan UWP Packaged Apps
        try
        {
            winrt::Windows::Management::Deployment::PackageManager packageManager;
            auto packages = packageManager.FindPackagesForUser(L"");
            for (auto const& package : packages)
            {
                if (!package.IsFramework())
                {
                    try
                    {
                        auto appEntries = package.GetAppListEntries();
                        for (auto const& entry : appEntries)
                        {
                            AppItem item;
                            item.Name = entry.DisplayInfo().DisplayName();
                            item.AUMID = entry.AppUserModelId();
                            item.IsUWP = true;
                            
                            // Get logo stream in background
                            item.Icon = GetUwpAppIcon(entry);
                            
                            // Filter out empty name apps
                            if (!item.Name.empty() && !item.AUMID.empty())
                            {
                                // Check if we already have this app
                                auto it = std::find_if(apps.begin(), apps.end(), [&](AppItem const& existing) {
                                    return existing.AUMID == item.AUMID || (existing.Name == item.Name && !existing.IsUWP);
                                });
                                if (it == apps.end())
                                {
                                    apps.push_back(item);
                                }
                            }
                        }
                    }
                    catch (...)
                    {
                        // Ignore package errors
                    }
                }
            }
        }
        catch (...)
        {
            // PackageManager might fail in some configurations
        }

        // Sort applications alphabetically
        std::sort(apps.begin(), apps.end(), [](AppItem const& a, AppItem const& b) {
            return a.Name < b.Name;
        });

        return apps;
    }

    void AppScanner::ScanStartMenuFolder(std::wstring const& folderPath, std::vector<AppItem>& apps)
    {
        if (!std::filesystem::exists(folderPath)) return;

        try
        {
            for (auto const& dirEntry : std::filesystem::recursive_directory_iterator(folderPath, std::filesystem::directory_options::skip_permission_denied))
            {
                if (dirEntry.is_regular_file())
                {
                    std::wstring path = dirEntry.path().wstring();
                    if (path.size() > 4 && path.substr(path.size() - 4) == L".lnk")
                    {
                        AppItem appItem;
                        if (ParseShortcut(path, appItem))
                        {
                            // Filter out uninstallers, manuals, readme etc.
                            std::wstring lowerName = appItem.Name;
                            std::transform(lowerName.begin(), lowerName.end(), lowerName.begin(), ::towlower);
                            
                            if (lowerName.find(L"uninstall") == std::wstring::npos &&
                                lowerName.find(L"read me") == std::wstring::npos &&
                                lowerName.find(L"readme") == std::wstring::npos &&
                                lowerName.find(L"documentation") == std::wstring::npos &&
                                lowerName.find(L"help") == std::wstring::npos &&
                                lowerName.find(L"manual") == std::wstring::npos)
                            {
                                // Avoid duplicates
                                auto it = std::find_if(apps.begin(), apps.end(), [&](AppItem const& existing) {
                                    return existing.Path == appItem.Path && existing.Name == appItem.Name;
                                });
                                if (it == apps.end())
                                {
                                    apps.push_back(appItem);
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (...)
        {
            // Path iteration errors
        }
    }

    bool AppScanner::ParseShortcut(std::wstring const& lnkPath, AppItem& appItem)
    {
        wil::com_ptr<IShellLinkW> psl;
        if (FAILED(CoCreateInstance(CLSID_ShellLink, NULL, CLSCTX_INPROC_SERVER, IID_IShellLinkW, psl.put_void())))
        {
            return false;
        }

        wil::com_ptr<IPersistFile> ppf;
        if (FAILED(psl->QueryInterface(IID_IPersistFile, ppf.put_void())))
        {
            return false;
        }

        if (FAILED(ppf->Load(lnkPath.c_str(), STGM_READ)))
        {
            return false;
        }

        // Get Name from the shortcut file name
        std::filesystem::path fsPath(lnkPath);
        appItem.Name = fsPath.stem().wstring();

        // Get Path
        WCHAR szTarget[MAX_PATH];
        psl->GetPath(szTarget, MAX_PATH, NULL, 0);
        appItem.Path = szTarget;

        // Extract description
        WCHAR szDesc[MAX_PATH];
        if (SUCCEEDED(psl->GetDescription(szDesc, MAX_PATH)))
        {
            // Can be used if needed
        }

        // Check if UWP AUMID is stored in the shortcut (e.g. for modern apps)
        wil::com_ptr<IPropertyStore> pPropStore;
        if (SUCCEEDED(psl->QueryInterface(IID_IPropertyStore, pPropStore.put_void())))
        {
            PROPVARIANT propvar;
            PropVariantInit(&propvar);
            if (SUCCEEDED(pPropStore->GetValue(PKEY_AppUserModel_ID, &propvar)))
            {
                if (propvar.vt == VT_LPWSTR && propvar.pwszVal != nullptr)
                {
                    appItem.AUMID = propvar.pwszVal;
                    appItem.IsUWP = true;
                }
                PropVariantClear(&propvar);
            }
        }

        // Extract Icon
        HICON hIcon = nullptr;
        // Try getting icon from the shortcut itself
        WCHAR szIconPath[MAX_PATH];
        int iconIndex = 0;
        if (SUCCEEDED(psl->GetIconLocation(szIconPath, MAX_PATH, &iconIndex)) && szIconPath[0] != L'\0')
        {
            // Expand environment variables in path
            WCHAR szExpandedPath[MAX_PATH];
            ExpandEnvironmentStringsW(szIconPath, szExpandedPath, MAX_PATH);
            ExtractIconExW(szExpandedPath, iconIndex, &hIcon, nullptr, 1);
        }

        // Fallback: Extract icon from the target executable
        if (!hIcon && !appItem.Path.empty())
        {
            ExtractIconExW(appItem.Path.c_str(), 0, &hIcon, nullptr, 1);
        }

        // Final Fallback: Shell File Info (handles folders, documents, etc.)
        if (!hIcon)
        {
            SHFILEINFOW sfi = {};
            if (SHGetFileInfoW(lnkPath.c_str(), 0, &sfi, sizeof(sfi), SHGFI_ICON | SHGFI_LARGEICON))
            {
                hIcon = sfi.hIcon;
            }
        }

        if (hIcon)
        {
            appItem.Icon = GetBitmapFromHIcon(hIcon);
            DestroyIcon(hIcon);
        }

        // Only return true if we have a path to run, or if it's UWP
        return !appItem.Path.empty() || appItem.IsUWP;
    }

    winrt::Windows::Graphics::Imaging::SoftwareBitmap AppScanner::GetBitmapFromHIcon(HICON hIcon)
    {
        if (!hIcon) return nullptr;

        ICONINFO iconInfo;
        if (!GetIconInfo(hIcon, &iconInfo)) return nullptr;

        wil::unique_hbitmap hbmColor(iconInfo.hbmColor);
        wil::unique_hbitmap hbmMask(iconInfo.hbmMask);

        BITMAP bm;
        GetObject(hbmColor.get(), sizeof(bm), &bm);

        int width = bm.bmWidth;
        int height = bm.bmHeight;

        try
        {
            winrt::Windows::Graphics::Imaging::SoftwareBitmap softwareBitmap(
                winrt::Windows::Graphics::Imaging::BitmapPixelFormat::Bgra8,
                width,
                height,
                winrt::Windows::Graphics::Imaging::BitmapAlphaMode::Premultiplied);

            auto buffer = softwareBitmap.LockBuffer(winrt::Windows::Graphics::Imaging::BitmapBufferAccessMode::Write);
            auto reference = buffer.CreateReference();
            auto byteAccess = reference.as<IMemoryBufferByteAccess>();

            byte* data = nullptr;
            uint32_t capacity = 0;
            if (SUCCEEDED(byteAccess->GetBuffer(&data, &capacity)))
            {
                BITMAPINFO bmi = {};
                bmi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
                bmi.bmiHeader.biWidth = width;
                bmi.bmiHeader.biHeight = -height; // Top-down
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biBitCount = 32;
                bmi.bmiHeader.biCompression = BI_RGB;

                HDC hdc = GetDC(nullptr);
                GetDIBits(hdc, hbmColor.get(), 0, height, data, &bmi, DIB_RGB_COLORS);
                ReleaseDC(nullptr, hdc);
                
                // Fix alpha channel if it's completely zero (some icons lack alpha information and will render invisible)
                bool hasAlpha = false;
                for (int i = 3; i < width * height * 4; i += 4)
                {
                    if (data[i] != 0)
                    {
                        hasAlpha = true;
                        break;
                    }
                }

                if (!hasAlpha)
                {
                    for (int i = 3; i < width * height * 4; i += 4)
                    {
                        data[i] = 255;
                    }
                }
            }

            return softwareBitmap;
        }
        catch (...)
        {
            return nullptr;
        }
    }

    winrt::Windows::Graphics::Imaging::SoftwareBitmap AppScanner::GetUwpAppIcon(winrt::Windows::ApplicationModel::Core::AppListEntry const& entry)
    {
        try
        {
            auto logoStreamRef = entry.DisplayInfo().GetLogo(winrt::Windows::Foundation::Size{ 44, 44 });
            if (!logoStreamRef) return nullptr;

            auto stream = logoStreamRef.OpenReadAsync().get();
            auto decoder = winrt::Windows::Graphics::Imaging::BitmapDecoder::CreateAsync(stream).get();
            auto softwareBitmap = decoder.GetSoftwareBitmapAsync().get();

            if (softwareBitmap.BitmapPixelFormat() != winrt::Windows::Graphics::Imaging::BitmapPixelFormat::Bgra8 ||
                softwareBitmap.BitmapAlphaMode() != winrt::Windows::Graphics::Imaging::BitmapAlphaMode::Premultiplied)
            {
                softwareBitmap = winrt::Windows::Graphics::Imaging::SoftwareBitmap::Convert(
                    softwareBitmap,
                    winrt::Windows::Graphics::Imaging::BitmapPixelFormat::Bgra8,
                    winrt::Windows::Graphics::Imaging::BitmapAlphaMode::Premultiplied);
            }
            return softwareBitmap;
        }
        catch (...)
        {
            return nullptr;
        }
    }

    bool AppScanner::Launch(AppItem const& item)
    {
        if (item.IsUWP)
        {
            wil::com_ptr<IApplicationActivationManager> pActivationManager;
            HRESULT hr = CoCreateInstance(
                CLSID_ApplicationActivationManager,
                NULL,
                CLSCTX_INPROC_SERVER,
                IID_IApplicationActivationManager,
                pActivationManager.put_void());

            if (SUCCEEDED(hr))
            {
                DWORD processId = 0;
                hr = pActivationManager->ActivateApplication(item.AUMID.c_str(), NULL, static_cast<ACTIVATEOPTIONS>(0), &processId);
                return SUCCEEDED(hr);
            }
            
            // Fallback via protocol
            try
            {
                std::wstring protocol = L"shell:AppsFolder\\" + item.AUMID;
                ShellExecuteW(NULL, L"open", protocol.c_str(), NULL, NULL, SW_SHOWNORMAL);
                return true;
            }
            catch (...)
            {
                return false;
            }
        }
        else
        {
            HINSTANCE hInst = ShellExecuteW(NULL, L"open", item.Path.c_str(), NULL, NULL, SW_SHOWNORMAL);
            return (reinterpret_cast<INT_PTR>(hInst) > 32);
        }
    }
}
