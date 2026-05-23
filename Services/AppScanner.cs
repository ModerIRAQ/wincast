using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Core;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Management.Deployment;
using WinCast.Models;

namespace WinCast.Services;

internal static class AppScanner
{
    [ComImport, Guid("5b0d3235-4dba-4d44-865e-8f1d0e4fd04d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMemoryBufferByteAccess
    {
        unsafe int GetBuffer(out byte* value, out uint capacity);
    }

    internal static List<AppItem> ScanApps()
    {
        var apps = new List<AppItem>();

        // Scan Win32 Start Menu shortcuts
        string systemStartMenu = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu) + "\\Programs";
        string userStartMenu = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu) + "\\Programs";

        ScanStartMenuFolder(systemStartMenu, apps);
        ScanStartMenuFolder(userStartMenu, apps);

        // Scan UWP packages
        try
        {
            var packageManager = new PackageManager();
            var packages = packageManager.FindPackagesForUser("");
            foreach (var package in packages)
            {
                if (package.IsFramework) continue;
                try
                {
                    if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                    {
                        foreach (var entry in package.GetAppListEntries())
                        {
                            string name = entry.DisplayInfo?.DisplayName ?? string.Empty;
                            string aumid = entry.AppUserModelId ?? string.Empty;
                            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(aumid)) continue;

                            SoftwareBitmap? icon = GetUwpAppIcon(entry);

                            // Dedup: skip if same AUMID exists, or same name with non-UWP already present
                            if (!apps.Any(a => a.AUMID == aumid || (a.Name == name && !a.IsUWP)))
                                apps.Add(new AppItem { Name = name, AUMID = aumid, Path = aumid, IsUWP = true, Icon = icon });
                        }
                    }
                }
                catch { /* Ignore per-package errors */ }
            }
        }
        catch { /* PackageManager might fail */ }

        apps.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return apps;
    }

    private static void ScanStartMenuFolder(string folderPath, List<AppItem> apps)
    {
        if (!Directory.Exists(folderPath)) return;

        try
        {
            foreach (string lnkPath in Directory.EnumerateFiles(folderPath, "*.lnk", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true
            }))
            {
                if (ParseShortcut(lnkPath, out AppItem appItem))
                {
                    string lowerName = appItem.Name.ToLowerInvariant();
                    if (!lowerName.Contains("uninstall") && !lowerName.Contains("read me") &&
                        !lowerName.Contains("readme") && !lowerName.Contains("documentation") &&
                        !lowerName.Contains("help") && !lowerName.Contains("manual"))
                    {
                        if (!apps.Any(a => a.Path == appItem.Path && a.Name == appItem.Name))
                            apps.Add(appItem);
                    }
                }
            }
        }
        catch { /* iteration errors */ }
    }

    private static bool ParseShortcut(string lnkPath, out AppItem appItem)
    {
        appItem = new AppItem();
        NativeMethods.IShellLinkW? shellLink = null;
        try
        {
            shellLink = (NativeMethods.IShellLinkW)new NativeMethods.ShellLinkWrapper();
            var persistFile = (NativeMethods.IPersistFile)shellLink;
            persistFile.Load(lnkPath, NativeMethods.STGM_READ);

            appItem.Name = Path.GetFileNameWithoutExtension(lnkPath);

            var sb = new StringBuilder(260);
            shellLink.GetPath(sb, 260, IntPtr.Zero, 0);
            appItem.Path = sb.ToString();

            // Try to get AUMID from property store
            NativeMethods.PropVariant propVar = default;
            bool propVarNeedsClear = false;
            try
            {
                if (shellLink is NativeMethods.IPropertyStore propStore)
                {
                    var pkey = new NativeMethods.PropertyKey
                    {
                        fmtid = NativeMethods.FMTID_PKEY_AppUserModel_ID,
                        pid = NativeMethods.PID_AppUserModel_ID
                    };
                    propStore.GetValue(ref pkey, out propVar);
                    propVarNeedsClear = true;
                    if (propVar.vt == NativeMethods.VT_LPWSTR && propVar.pwszVal != IntPtr.Zero)
                    {
                        appItem.AUMID = Marshal.PtrToStringUni(propVar.pwszVal) ?? string.Empty;
                        appItem.IsUWP = true;
                    }
                }
            }
            catch { /* Property store might not be available */ }
            finally
            {
                if (propVarNeedsClear)
                {
                    NativeMethods.PropVariantClear(ref propVar);
                }
            }

            // Extract icon
            appItem.Icon = ExtractShortcutIcon(shellLink, lnkPath, appItem.Path);

            return !string.IsNullOrEmpty(appItem.Path) || appItem.IsUWP;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (shellLink != null)
            {
                Marshal.ReleaseComObject(shellLink);
            }
        }
    }

    private static SoftwareBitmap? ExtractShortcutIcon(NativeMethods.IShellLinkW shellLink, string lnkPath, string targetPath)
    {
        IntPtr hIcon = IntPtr.Zero;
        try
        {
            // Try shortcut icon location first
            var sb = new StringBuilder(260);
            shellLink.GetIconLocation(sb, 260, out int iconIndex);
            if (sb.Length > 0)
            {
                string expandedPath = Environment.ExpandEnvironmentVariables(sb.ToString());
                NativeMethods.ExtractIconExW(expandedPath, iconIndex, out hIcon, out _, 1);
            }

            // Fallback: extract from target executable
            if (hIcon == IntPtr.Zero && !string.IsNullOrEmpty(targetPath))
                NativeMethods.ExtractIconExW(targetPath, 0, out hIcon, out _, 1);

            // Final fallback: shell file info
            if (hIcon == IntPtr.Zero)
            {
                var sfi = new NativeMethods.SHFILEINFOW();
                NativeMethods.SHGetFileInfoW(lnkPath, 0, ref sfi, (uint)Marshal.SizeOf(sfi),
                    NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON);
                hIcon = sfi.hIcon;
            }

            if (hIcon != IntPtr.Zero)
                return GetBitmapFromHIcon(hIcon);

            return null;
        }
        finally
        {
            if (hIcon != IntPtr.Zero)
                NativeMethods.DestroyIcon(hIcon);
        }
    }

    private static SoftwareBitmap? GetBitmapFromHIcon(IntPtr hIcon)
    {
        if (hIcon == IntPtr.Zero) return null;

        if (!NativeMethods.GetIconInfo(hIcon, out var iconInfo)) return null;
        try
        {
            NativeMethods.GetObject(iconInfo.hbmColor, Marshal.SizeOf<NativeMethods.BITMAP>(), out var bm);
            int width = bm.bmWidth;
            int height = bm.bmHeight;
            if (width <= 0 || height <= 0) return null;

            var bmi = new NativeMethods.BITMAPINFO();
            bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>();
            bmi.bmiHeader.biWidth = width;
            bmi.bmiHeader.biHeight = -height; // top-down
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = NativeMethods.BI_RGB;

            byte[] pixelData = new byte[width * height * 4];

            IntPtr hdc = NativeMethods.GetDC(IntPtr.Zero);
            try
            {
                NativeMethods.GetDIBits(hdc, iconInfo.hbmColor, 0, (uint)height, pixelData, ref bmi, NativeMethods.DIB_RGB_COLORS);
            }
            finally
            {
                NativeMethods.ReleaseDC(IntPtr.Zero, hdc);
            }

            // Fix alpha channel: if all alpha bytes are 0, set them to 255
            int pixelCount = width * height;
            bool hasAlpha = false;
            for (int i = 3; i < pixelCount * 4; i += 4)
            {
                if (pixelData[i] != 0) { hasAlpha = true; break; }
            }
            if (!hasAlpha)
            {
                for (int i = 3; i < pixelCount * 4; i += 4)
                    pixelData[i] = 255;
            }

            // Convert byte array to SoftwareBitmap via stream
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var encoder = BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream).GetAwaiter().GetResult();
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)width, (uint)height, 96.0, 96.0, pixelData);
            encoder.FlushAsync().GetAwaiter().GetResult();

            stream.Seek(0);
            var decoder = BitmapDecoder.CreateAsync(stream).GetAwaiter().GetResult();
            return decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied).GetAwaiter().GetResult();
        }
        finally
        {
            if (iconInfo.hbmColor != IntPtr.Zero)
                NativeMethods.DeleteObject(iconInfo.hbmColor);
            if (iconInfo.hbmMask != IntPtr.Zero)
                NativeMethods.DeleteObject(iconInfo.hbmMask);
        }
    }

    private static SoftwareBitmap? GetUwpAppIcon(AppListEntry entry)
    {
        try
        {
            var logoStreamRef = entry.DisplayInfo?.GetLogo(new Size(44, 44));
            if (logoStreamRef == null) return null;

            using var stream = logoStreamRef.OpenReadAsync().GetAwaiter().GetResult();
            var decoder = BitmapDecoder.CreateAsync(stream).GetAwaiter().GetResult();
            var softwareBitmap = decoder.GetSoftwareBitmapAsync().GetAwaiter().GetResult();

            if (softwareBitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 ||
                softwareBitmap.BitmapAlphaMode != BitmapAlphaMode.Premultiplied)
            {
                softwareBitmap = SoftwareBitmap.Convert(softwareBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            }
            return softwareBitmap;
        }
        catch { return null; }
    }

    internal static bool Launch(AppItem item)
    {
        if (item.IsUWP)
        {
            NativeMethods.IApplicationActivationManager? manager = null;
            try
            {
                var clsidType = Type.GetTypeFromCLSID(NativeMethods.CLSID_ApplicationActivationManager);
                if (clsidType != null)
                {
                    manager = Activator.CreateInstance(clsidType) as NativeMethods.IApplicationActivationManager;
                    if (manager != null)
                    {
                        manager.ActivateApplication(item.AUMID, null, 0, out _);
                        return true;
                    }
                }
                throw new InvalidOperationException("Failed to activate application using ActivationManager.");
            }
            catch
            {
                try
                {
                    Process.Start(new ProcessStartInfo("shell:AppsFolder\\" + item.AUMID) { UseShellExecute = true });
                    return true;
                }
                catch { return false; }
            }
            finally
            {
                if (manager != null)
                {
                    Marshal.ReleaseComObject(manager);
                }
            }
        }
        else
        {
            try
            {
                Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
                return true;
            }
            catch { return false; }
        }
    }
}
