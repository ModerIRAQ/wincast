using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace WinCast.Models;

internal class AppItem
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string AUMID { get; set; } = string.Empty;
    public bool IsUWP { get; set; }
    public SoftwareBitmap? Icon { get; set; }
    public SoftwareBitmapSource? CachedIconSource { get; set; }
}
