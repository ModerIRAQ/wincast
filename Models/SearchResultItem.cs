using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace WinCast.Models;

public class SearchResultItem
{
    public string Name { get; }
    public string Path { get; }
    public string AUMID { get; }
    public bool IsUWP { get; }
    public bool IsCalculator { get; }
    public string CalcResult { get; }
    public bool IsShellCommand { get; }
    public string ShellCommandText { get; }
    public bool IsHelp { get; }
    public string HelpDetail { get; }
    public SoftwareBitmapSource? IconSource { get; }
    public string Category { get; }

    // For category header rows injected into the list
    public bool IsHeader { get; }
    public string HeaderText { get; }

    // For recent app items shown in dashboard
    public bool IsRecent { get; set; }

    public SearchResultItem(
        string name,
        string path,
        string aumid,
        bool isUwp,
        bool isCalculator,
        string calcResult,
        SoftwareBitmapSource? iconSource,
        string category = "Applications",
        bool isShellCommand = false,
        string shellCommandText = "",
        bool isHelp = false,
        string helpDetail = "")
    {
        Name = name;
        Path = path;
        AUMID = aumid;
        IsUWP = isUwp;
        IsCalculator = isCalculator;
        CalcResult = calcResult;
        IconSource = iconSource;
        Category = category;
        IsHeader = false;
        HeaderText = string.Empty;
        IsShellCommand = isShellCommand;
        ShellCommandText = shellCommandText;
        IsHelp = isHelp;
        HelpDetail = helpDetail;
    }

    // Header-only constructor
    private SearchResultItem(string headerText)
    {
        Name = headerText;
        Path = string.Empty;
        AUMID = string.Empty;
        CalcResult = string.Empty;
        HeaderText = headerText;
        Category = string.Empty;
        IsHeader = true;
        IsShellCommand = false;
        ShellCommandText = string.Empty;
        IsHelp = false;
        HelpDetail = string.Empty;
    }

    public static SearchResultItem CreateHeader(string text) => new(text);

    public Visibility CalcVisibility =>
        IsCalculator ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ShellVisibility =>
        IsShellCommand ? Visibility.Visible : Visibility.Collapsed;

    public Visibility HelpVisibility =>
        IsHelp ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ResultIconVisibility =>
        IsHelp ? Visibility.Collapsed : Visibility.Visible;

    public Visibility UwpVisibility =>
        (IsCalculator || IsShellCommand) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ImageVisibility =>
        (IconSource != null && !IsCalculator && !IsShellCommand && !IsHelp) ? Visibility.Visible : Visibility.Collapsed;

    public Visibility HeaderVisibility =>
        IsHeader ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ContentVisibility =>
        IsHeader ? Visibility.Collapsed : Visibility.Visible;

    public string AppTypeBadge => IsCalculator ? "Calculator" : (IsShellCommand ? "Command" : (IsHelp ? "Help" : (IsUWP ? "UWP" : "Win32")));
}
