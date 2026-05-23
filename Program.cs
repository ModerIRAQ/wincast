using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace WinCast;

internal static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static App? _app;

    [STAThread]
    static void Main(string[] args)
    {
        // Single instance check
        using var mutex = new Mutex(true, @"Local\WinCastSingleInstanceMutex", out bool createdNew);
        if (!createdNew)
        {
            IntPtr hWnd = FindWindowW(null, "WinCast");
            if (hWnd != IntPtr.Zero)
                PostMessageW(hWnd, NativeMethods.WM_USER + 1, IntPtr.Zero, IntPtr.Zero);
            return;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _app = new App();
        });
    }
}
