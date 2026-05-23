using System;
using System.Linq;
using Microsoft.UI.Xaml;

namespace WinCast;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        this.UnhandledException += (sender, e) =>
        {
            System.Diagnostics.Debug.WriteLine($"Unhandled exception: {e.Exception}");
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();

        if (Environment.GetCommandLineArgs().Contains("--background"))
        {
            var hwnd = WindowHelper.GetHWND(_window);
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
        }
    }
}
