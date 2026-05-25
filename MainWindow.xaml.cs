using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Microsoft.UI.Xaml.Media.Imaging;
using WinCast.Models;
using WinCast.Services;

namespace WinCast;

public sealed partial class MainWindow : Window
{
    private IntPtr _hwnd;
    private List<AppItem> _apps = new();
    private readonly ObservableCollection<SearchResultItem> _searchResults = new();
    private List<RecentAppEntry> _recentEntries = new();
    private CancellationTokenSource? _searchCts;
    private bool _isScanning;
    private bool _scanPending;
    private bool _isVisible = true;
    private bool _isDashboardVisible = true;
    private bool _isSettingsVisible = false;
    private NativeMethods.SUBCLASSPROC? _subclassDelegate;

    private FileSystemWatcher? _userMenuWatcher;
    private FileSystemWatcher? _commonMenuWatcher;
    private CancellationTokenSource? _debounceCts;
    private UpdateInfo? _availableUpdate;
    private bool _isUpdateDownloading;

    public MainWindow()
    {
        InitializeComponent();

        _hwnd = WindowHelper.GetHWND(this);
        WindowHelper.MakeBorderless(_hwnd);
        WindowHelper.CenterWindow(_hwnd, 800, 530);

        // Apply system backdrop from settings
        ApplyVisualTheme();

        ResultsListView.ItemsSource = _searchResults;

        // Register global hotkey: Alt+Space
        NativeMethods.RegisterHotKey(_hwnd, 1, NativeMethods.MOD_ALT, NativeMethods.VK_SPACE);

        _subclassDelegate = (IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr idSubclass, IntPtr refData) =>
            OnWndProc(hWnd, msg, wParam, lParam);
        NativeMethods.SetWindowSubclass(_hwnd, _subclassDelegate, 1, IntPtr.Zero);

        this.Closed += (s, e) =>
        {
            NativeMethods.UnregisterHotKey(_hwnd, 1);
            if (_subclassDelegate != null)
                NativeMethods.RemoveWindowSubclass(_hwnd, _subclassDelegate, 1);
            _userMenuWatcher?.Dispose();
            _commonMenuWatcher?.Dispose();
            _debounceCts?.Cancel();
            RemoveTrayIcon();
        };

        // Load recents before first display
        _recentEntries = RecentAppsService.Load();

        AddTrayIcon();
        ShowDashboard(animate: false);
        RefreshGreeting();
        RefreshDashboardStats();

        StartAppScan();
        SetupStartMenuWatchers();
        SearchBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);

        // Global Escape key handler for Settings Panel
        this.Content.KeyDown += (s, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape && _isSettingsVisible)
            {
                ShowSettings(false);
                e.Handled = true;
            }
        };

        // Bind settings event handlers programmatically
        SettingsButton.Click += SettingsButton_Click;
        BackToMainButton.Click += BackToMainButton_Click;
        ShowPreviewToggle.Toggled += ShowPreviewToggle_Toggled;
        LaunchOnStartupToggle.Toggled += LaunchOnStartupToggle_Toggled;
        ThemeModeComboBox.SelectionChanged += ThemeModeComboBox_SelectionChanged;
        BackdropComboBox.SelectionChanged += BackdropComboBox_SelectionChanged;
        SurfaceOpacityComboBox.SelectionChanged += SurfaceOpacityComboBox_SelectionChanged;
        SettingsTabButton.Click += (s, e) => SwitchSettingsTab(true);
        HelpTabButton.Click += (s, e) => SwitchSettingsTab(false);

        _ = CheckForUpdatesInBackgroundAsync();
    }

    // ═══════════════════════════════════════════════════
    //  Window Procedure
    // ═══════════════════════════════════════════════════

    private IntPtr OnWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case NativeMethods.WM_HOTKEY when wParam.ToInt32() == 1:
                ToggleVisibility(!_isVisible);
                return IntPtr.Zero;

            case NativeMethods.WM_ACTIVATE:
                if (wParam.ToInt32() == NativeMethods.WA_INACTIVE)
                    ToggleVisibility(false);
                break;

            case NativeMethods.WM_USER + 1:
                ToggleVisibility(true);
                return IntPtr.Zero;

            case NativeMethods.WM_TRAYICON:
                uint eventType = (uint)lParam.ToInt32();
                if (eventType == NativeMethods.WM_LBUTTONUP)
                {
                    ToggleVisibility(!_isVisible);
                    return IntPtr.Zero;
                }
                else if (eventType == NativeMethods.WM_RBUTTONUP)
                {
                    ShowTrayContextMenu();
                    return IntPtr.Zero;
                }
                break;
        }
        return NativeMethods.DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    // ═══════════════════════════════════════════════════
    //  App Scanning
    // ═══════════════════════════════════════════════════

    private async void StartAppScan()
    {
        if (_isScanning)
        {
            _scanPending = true;
            return;
        }
        _isScanning = true;

        // Show loading state
        DispatcherQueue.TryEnqueue(() =>
        {
            IndexingRing.Visibility = Visibility.Visible;
            IndexingRing.IsActive = true;
            FooterStatusText.Text = "Indexing apps…";
            DashboardStatusText.Text = "Indexing apps";
        });

        do
        {
            _scanPending = false;

            var apps = await Task.Run(() =>
            {
                List<AppItem>? result = null;
                var thread = new Thread(() => { result = AppScanner.ScanApps(); });
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                thread.Join();
                return result ?? new List<AppItem>();
            });

            _apps = apps;

            DispatcherQueue.TryEnqueue(() =>
            {
                IndexingRing.Visibility = Visibility.Collapsed;
                IndexingRing.IsActive = false;
                FooterStatusText.Text = $"{_apps.Count} apps indexed";
                RefreshDashboardStats();
                UpdateSearch(SearchBox.Text);
                // Re-bind recent items now that we have icons
                RefreshRecentItems();
            });

        } while (_scanPending);

        _isScanning = false;
    }

    private void SetupStartMenuWatchers()
    {
        try
        {
            string systemStartMenu = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu) + "\\Programs";
            string userStartMenu = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu) + "\\Programs";

            if (Directory.Exists(systemStartMenu))
            {
                _commonMenuWatcher = new FileSystemWatcher(systemStartMenu)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
                    Filter = "*.lnk"
                };
                _commonMenuWatcher.Created += OnStartMenuChanged;
                _commonMenuWatcher.Deleted += OnStartMenuChanged;
                _commonMenuWatcher.Changed += OnStartMenuChanged;
                _commonMenuWatcher.Renamed += OnStartMenuChanged;
                _commonMenuWatcher.EnableRaisingEvents = true;
            }

            if (Directory.Exists(userStartMenu))
            {
                _userMenuWatcher = new FileSystemWatcher(userStartMenu)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
                    Filter = "*.lnk"
                };
                _userMenuWatcher.Created += OnStartMenuChanged;
                _userMenuWatcher.Deleted += OnStartMenuChanged;
                _userMenuWatcher.Changed += OnStartMenuChanged;
                _userMenuWatcher.Renamed += OnStartMenuChanged;
                _userMenuWatcher.EnableRaisingEvents = true;
            }
        }
        catch { /* non-critical */ }
    }

    private void OnStartMenuChanged(object sender, FileSystemEventArgs e)
    {
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        _ = Task.Delay(1000, token).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && !token.IsCancellationRequested)
                DispatcherQueue.TryEnqueue(StartAppScan);
        }, TaskScheduler.Default);
    }

    // ═══════════════════════════════════════════════════
    //  Dashboard / Results View Switching
    // ═══════════════════════════════════════════════════

    private void ShowDashboard(bool animate = true)
    {
        SetPreviewPaneVisibility(false);
        SettingsPanel.Visibility = Visibility.Collapsed;
        _isSettingsVisible = false;
        if (_isDashboardVisible) return;
        _isDashboardVisible = true;

        if (animate)
        {
            // Fade out results
            var fadeOut = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(120), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            var sbOut = new Storyboard();
            Storyboard.SetTarget(fadeOut, ResultsPanel);
            Storyboard.SetTargetProperty(fadeOut, "Opacity");
            sbOut.Children.Add(fadeOut);
            sbOut.Completed += (_, __) =>
            {
                ResultsPanel.Visibility = Visibility.Collapsed;
                // Fade in dashboard
                DashboardPanel.Visibility = Visibility.Visible;
                var fadeIn = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(180), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                var sbIn = new Storyboard();
                Storyboard.SetTarget(fadeIn, DashboardPanel);
                Storyboard.SetTargetProperty(fadeIn, "Opacity");
                sbIn.Children.Add(fadeIn);
                sbIn.Begin();
            };
            sbOut.Begin();
        }
        else
        {
            ResultsPanel.Visibility = Visibility.Collapsed;
            ResultsPanel.Opacity = 0;
            DashboardPanel.Visibility = Visibility.Visible;
            DashboardPanel.Opacity = 1;
        }
    }

    private void ShowResults(bool animate = true)
    {
        if (!_isDashboardVisible) return;
        _isDashboardVisible = false;
        SettingsPanel.Visibility = Visibility.Collapsed;
        _isSettingsVisible = false;

        if (animate)
        {
            var fadeOut = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(100), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            var sbOut = new Storyboard();
            Storyboard.SetTarget(fadeOut, DashboardPanel);
            Storyboard.SetTargetProperty(fadeOut, "Opacity");
            sbOut.Children.Add(fadeOut);
            sbOut.Completed += (_, __) =>
            {
                DashboardPanel.Visibility = Visibility.Collapsed;
                ResultsPanel.Visibility = Visibility.Visible;
                var fadeIn = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(160), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                var sbIn = new Storyboard();
                Storyboard.SetTarget(fadeIn, ResultsPanel);
                Storyboard.SetTargetProperty(fadeIn, "Opacity");
                sbIn.Children.Add(fadeIn);
                sbIn.Begin();
            };
            sbOut.Begin();
        }
        else
        {
            DashboardPanel.Visibility = Visibility.Collapsed;
            DashboardPanel.Opacity = 0;
            ResultsPanel.Visibility = Visibility.Visible;
            ResultsPanel.Opacity = 1;
        }
    }

    // ═══════════════════════════════════════════════════
    //  Search
    // ═══════════════════════════════════════════════════

    private void UpdateSearch(string query)
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        _ = UpdateSearchAsync(query, _searchCts.Token);
    }

    private async Task UpdateSearchAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            if (ct.IsCancellationRequested) return;
            _searchResults.Clear();
            DispatcherQueue.TryEnqueue(() => ShowDashboard());
            return;
        }

        var rawResults = await Task.Run(() => SearchEngine.Search(_apps, query), ct);
        if (ct.IsCancellationRequested) return;

        // Switch to results view
        if (_isDashboardVisible)
            ShowResults();

        _searchResults.Clear();

        if (rawResults.Count == 0)
        {
            EmptyResultsPanel.Visibility = Visibility.Visible;
            EmptyResultsText.Text = $"No results for \"{query}\"";
            SetPreviewPaneVisibility(false);
            return;
        }

        EmptyResultsPanel.Visibility = Visibility.Collapsed;

        // Group by category with headers
        string lastCategory = string.Empty;
        int count = Math.Min(15, rawResults.Count);
        for (int i = 0; i < count; i++)
        {
            var res = rawResults[i];
            string category = res.IsCalculator ? "Calculator" : (res.IsShellCommand ? "Command" : (res.IsHelp ? "Help & Shortcuts" : "Applications"));

            if (category != lastCategory)
            {
                _searchResults.Add(SearchResultItem.CreateHeader(category.ToUpperInvariant()));
                lastCategory = category;
            }

            SoftwareBitmapSource? iconSource = null;
            if (res.Item.Icon != null)
            {
                if (res.Item.CachedIconSource == null)
                {
                    var source = new SoftwareBitmapSource();
                    await source.SetBitmapAsync(res.Item.Icon);
                    res.Item.CachedIconSource = source;
                }
                iconSource = res.Item.CachedIconSource;
            }

            if (ct.IsCancellationRequested) return;

            _searchResults.Add(new SearchResultItem(
                res.Item.Name,
                res.Item.Path,
                res.Item.AUMID,
                res.Item.IsUWP,
                res.IsCalculator,
                res.CalcResult,
                iconSource,
                category,
                res.IsShellCommand,
                res.ShellCommandText,
                res.IsHelp,
                res.HelpDetail));
        }

        // Auto-select first non-header item
        if (_searchResults.Count > 0)
        {
            int firstContent = 0;
            for (int i = 0; i < _searchResults.Count; i++)
            {
                if (!_searchResults[i].IsHeader) { firstContent = i; break; }
            }
            ResultsListView.SelectedIndex = firstContent;
            SetPreviewPaneVisibility(true);
        }
        else
        {
            SetPreviewPaneVisibility(false);
        }
    }

    // ═══════════════════════════════════════════════════
    //  Detail Panel
    // ═══════════════════════════════════════════════════

    private void ResultsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Skip headers
        if (ResultsListView.SelectedItem is SearchResultItem item && item.IsHeader)
        {
            // Try to select next non-header
            int idx = ResultsListView.SelectedIndex + 1;
            while (idx < _searchResults.Count && _searchResults[idx].IsHeader) idx++;
            if (idx < _searchResults.Count)
                ResultsListView.SelectedIndex = idx;
            return;
        }
        UpdateDetailPanel();
    }

    private void UpdateDetailPanel()
    {
        if (ResultsListView.SelectedItem is not SearchResultItem selectedItem || selectedItem.IsHeader)
        {
            SetPreviewPaneVisibility(false);
            return;
        }

        SetPreviewPaneVisibility(true);

        // Reset default action labels
        ActionOpenText.Text = "Open";
        ActionCopyPathText.Text = "Copy Path";

        // Reset all detail icon visibilities
        DetailCalcIcon.Visibility = Visibility.Collapsed;
        DetailHelpIcon.Visibility = Visibility.Collapsed;
        DetailShellIcon.Visibility = Visibility.Collapsed;
        DetailSystemIcon.Visibility = Visibility.Collapsed;
        DetailWebIcon.Visibility = Visibility.Collapsed;
        DetailSearchIcon.Visibility = Visibility.Collapsed;
        DetailAppIcon.Visibility = Visibility.Collapsed;

        if (selectedItem.IsCalculator)
        {
            DetailCalcIcon.Visibility = Visibility.Visible;
            DetailTitleText.Text = "Calculator";
            TypeBadgeText.Text = "Calculator";
            TypeBadge.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x4A, 0x3F, 0xAA));

            DetailPathContainer.Visibility = Visibility.Collapsed;
            DetailAumidContainer.Visibility = Visibility.Collapsed;
            DetailCommandContainer.Visibility = Visibility.Collapsed;
            DetailHelpContainer.Visibility = Visibility.Collapsed;
            DetailEquationContainer.Visibility = Visibility.Visible;
            DetailResultContainer.Visibility = Visibility.Visible;
            DetailEquationText.Text = selectedItem.Path;
            DetailResultText.Text = selectedItem.CalcResult;

            ActionOpenGrid.Visibility = Visibility.Collapsed;
            ActionCopyResultGrid.Visibility = Visibility.Visible;
            ActionAdminGrid.Visibility = Visibility.Collapsed;
            ActionLocationGrid.Visibility = Visibility.Collapsed;
            ActionCopyPathGrid.Visibility = Visibility.Collapsed;
        }
        else if (selectedItem.IsShellCommand)
        {
            DetailShellIcon.Visibility = Visibility.Visible;
            DetailTitleText.Text = "Terminal Command";
            TypeBadgeText.Text = "Command";
            TypeBadge.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x0F, 0x76, 0x6E));

            DetailPathContainer.Visibility = Visibility.Collapsed;
            DetailAumidContainer.Visibility = Visibility.Collapsed;
            DetailEquationContainer.Visibility = Visibility.Collapsed;
            DetailResultContainer.Visibility = Visibility.Collapsed;
            DetailCommandContainer.Visibility = Visibility.Visible;
            DetailCommandText.Text = selectedItem.ShellCommandText;
            DetailHelpContainer.Visibility = Visibility.Collapsed;

            ActionOpenGrid.Visibility = Visibility.Visible;
            ActionOpenText.Text = "Execute Command";
            ActionCopyResultGrid.Visibility = Visibility.Collapsed;
            ActionAdminGrid.Visibility = Visibility.Visible;
            ActionLocationGrid.Visibility = Visibility.Collapsed;
            ActionCopyPathGrid.Visibility = Visibility.Visible;
            ActionCopyPathText.Text = "Copy Command";
        }
        else if (selectedItem.IsHelp)
        {
            DetailHelpIcon.Visibility = Visibility.Visible;
            DetailTitleText.Text = selectedItem.Name;
            TypeBadgeText.Text = "Help";
            TypeBadge.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x8B, 0x7F, 0xF0));

            DetailPathContainer.Visibility = Visibility.Collapsed;
            DetailAumidContainer.Visibility = Visibility.Collapsed;
            DetailEquationContainer.Visibility = Visibility.Collapsed;
            DetailResultContainer.Visibility = Visibility.Collapsed;
            DetailCommandContainer.Visibility = Visibility.Collapsed;
            DetailHelpContainer.Visibility = Visibility.Visible;
            DetailHelpText.Text = selectedItem.HelpDetail;

            ActionOpenGrid.Visibility = Visibility.Visible;
            ActionOpenText.Text = "Copy Description";
            ActionCopyResultGrid.Visibility = Visibility.Collapsed;
            ActionAdminGrid.Visibility = Visibility.Collapsed;
            ActionLocationGrid.Visibility = Visibility.Collapsed;
            ActionCopyPathGrid.Visibility = Visibility.Collapsed;
        }
        else if (selectedItem.IsWebUrl)
        {
            DetailWebIcon.Visibility = Visibility.Visible;
            DetailTitleText.Text = selectedItem.Name;
            TypeBadgeText.Text = "Web";
            TypeBadge.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x03, 0x69, 0xA1));

            DetailPathContainer.Visibility = Visibility.Visible;
            DetailPathText.Text = selectedItem.WebUrl;
            DetailAumidContainer.Visibility = Visibility.Collapsed;
            DetailEquationContainer.Visibility = Visibility.Collapsed;
            DetailResultContainer.Visibility = Visibility.Collapsed;
            DetailCommandContainer.Visibility = Visibility.Collapsed;
            DetailHelpContainer.Visibility = Visibility.Collapsed;

            ActionOpenGrid.Visibility = Visibility.Visible;
            ActionOpenText.Text = "Open Link";
            ActionCopyResultGrid.Visibility = Visibility.Collapsed;
            ActionAdminGrid.Visibility = Visibility.Collapsed;
            ActionLocationGrid.Visibility = Visibility.Collapsed;
            ActionCopyPathGrid.Visibility = Visibility.Visible;
            ActionCopyPathText.Text = "Copy URL";
        }
        else if (selectedItem.IsWebSearch)
        {
            DetailSearchIcon.Visibility = Visibility.Visible;
            DetailTitleText.Text = selectedItem.Name;
            TypeBadgeText.Text = "Search";
            TypeBadge.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xD9, 0x77, 0x06));

            DetailPathContainer.Visibility = Visibility.Visible;
            DetailPathText.Text = selectedItem.Path;
            DetailAumidContainer.Visibility = Visibility.Collapsed;
            DetailEquationContainer.Visibility = Visibility.Collapsed;
            DetailResultContainer.Visibility = Visibility.Collapsed;
            DetailCommandContainer.Visibility = Visibility.Collapsed;
            DetailHelpContainer.Visibility = Visibility.Collapsed;

            ActionOpenGrid.Visibility = Visibility.Visible;
            ActionOpenText.Text = "Search Google";
            ActionCopyResultGrid.Visibility = Visibility.Collapsed;
            ActionAdminGrid.Visibility = Visibility.Collapsed;
            ActionLocationGrid.Visibility = Visibility.Collapsed;
            ActionCopyPathGrid.Visibility = Visibility.Visible;
            ActionCopyPathText.Text = "Copy URL";
        }
        else if (selectedItem.IsSystemCommand)
        {
            DetailSystemIcon.Visibility = Visibility.Visible;
            DetailTitleText.Text = selectedItem.Name;
            TypeBadgeText.Text = "System";
            TypeBadge.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xBE, 0x12, 0x3C));

            DetailPathContainer.Visibility = Visibility.Visible;
            DetailPathText.Text = selectedItem.Path;
            DetailAumidContainer.Visibility = Visibility.Collapsed;
            DetailEquationContainer.Visibility = Visibility.Collapsed;
            DetailResultContainer.Visibility = Visibility.Collapsed;
            DetailCommandContainer.Visibility = Visibility.Collapsed;
            DetailHelpContainer.Visibility = Visibility.Collapsed;

            ActionOpenGrid.Visibility = Visibility.Visible;
            ActionOpenText.Text = "Execute Command";
            ActionCopyResultGrid.Visibility = Visibility.Collapsed;
            ActionAdminGrid.Visibility = Visibility.Collapsed;
            ActionLocationGrid.Visibility = Visibility.Collapsed;
            ActionCopyPathGrid.Visibility = Visibility.Collapsed;
        }
        else
        {
            DetailAppIcon.Visibility = Visibility.Visible;
            DetailAppIcon.Source = selectedItem.IconSource;
            DetailTitleText.Text = selectedItem.Name;

            DetailEquationContainer.Visibility = Visibility.Collapsed;
            DetailResultContainer.Visibility = Visibility.Collapsed;
            DetailCommandContainer.Visibility = Visibility.Collapsed;
            DetailHelpContainer.Visibility = Visibility.Collapsed;

            if (selectedItem.IsUWP)
            {
                TypeBadgeText.Text = "UWP";
                TypeBadge.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x0E, 0x74, 0x90));
                DetailPathContainer.Visibility = Visibility.Collapsed;
                DetailAumidContainer.Visibility = Visibility.Visible;
                DetailAumidText.Text = selectedItem.AUMID;

                ActionOpenGrid.Visibility = Visibility.Visible;
                ActionCopyResultGrid.Visibility = Visibility.Collapsed;
                ActionAdminGrid.Visibility = Visibility.Collapsed;
                ActionLocationGrid.Visibility = Visibility.Collapsed;
                ActionCopyPathGrid.Visibility = Visibility.Visible;
            }
            else
            {
                TypeBadgeText.Text = "Win32";
                TypeBadge.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x2E, 0x22, 0x70));
                DetailPathContainer.Visibility = Visibility.Visible;
                DetailPathText.Text = selectedItem.Path;
                DetailAumidContainer.Visibility = Visibility.Collapsed;

                ActionOpenGrid.Visibility = Visibility.Visible;
                ActionCopyResultGrid.Visibility = Visibility.Collapsed;
                ActionAdminGrid.Visibility = Visibility.Visible;
                ActionLocationGrid.Visibility = Visibility.Visible;
                ActionCopyPathGrid.Visibility = Visibility.Visible;
            }
        }
    }

    private void ResultsListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
        {
            args.ItemContainer.Opacity = 1;
            args.ItemContainer.RenderTransform = null;
            return;
        }

        if (args.Phase == 0)
        {
            var container = args.ItemContainer;
            int index = args.ItemIndex;

            if (index < 8)
            {
                container.Opacity = 0;
                var transform = new CompositeTransform { TranslateY = 8 };
                container.RenderTransform = transform;

                var sb = new Storyboard();

                var opacityAnim = new DoubleAnimation
                {
                    From = 0,
                    To = 1,
                    Duration = new Duration(TimeSpan.FromMilliseconds(150)),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(opacityAnim, container);
                Storyboard.SetTargetProperty(opacityAnim, "Opacity");
                sb.Children.Add(opacityAnim);

                var translateAnim = new DoubleAnimation
                {
                    From = 8,
                    To = 0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(180)),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(translateAnim, transform);
                Storyboard.SetTargetProperty(translateAnim, "TranslateY");
                sb.Children.Add(translateAnim);

                sb.BeginTime = TimeSpan.FromMilliseconds(index * 35);
                sb.Begin();
            }
            else
            {
                container.Opacity = 1;
                container.RenderTransform = null;
            }
        }
    }

    private async Task ExecuteSystemCommandAsync(SearchResultItem selected)
    {
        bool isDestructive = selected.SystemAction is "shutdown" or "restart" or "signout" or "emptybin";
        if (isDestructive)
        {
            ContentDialog confirmDialog = new ContentDialog
            {
                Title = $"{selected.Name}?",
                Content = $"Are you sure you want to perform this action: {selected.Name}?",
                PrimaryButtonText = selected.Name,
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.Content.XamlRoot
            };

            var res = await confirmDialog.ShowAsync();
            if (res != ContentDialogResult.Primary)
            {
                return;
            }
        }

        ToggleVisibility(false);

        _ = Task.Run(() =>
        {
            try
            {
                switch (selected.SystemAction)
                {
                    case "sleep":
                        NativeMethods.SetSuspendState(false, true, true);
                        break;
                    case "shutdown":
                        Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 0") { CreateNoWindow = true, UseShellExecute = false });
                        break;
                    case "restart":
                        Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0") { CreateNoWindow = true, UseShellExecute = false });
                        break;
                    case "lock":
                        NativeMethods.LockWorkStation();
                        break;
                    case "signout":
                        Process.Start(new ProcessStartInfo("logoff.exe") { CreateNoWindow = true, UseShellExecute = false });
                        break;
                    case "emptybin":
                        NativeMethods.SHEmptyRecycleBin(IntPtr.Zero, null, NativeMethods.SHERB_NOCONFIRMATION | NativeMethods.SHERB_NOPROGRESSUI | NativeMethods.SHERB_NOSOUND);
                        break;
                    case "screenoff":
                        NativeMethods.SendMessage(NativeMethods.HWND_BROADCAST, NativeMethods.WM_SYSCOMMAND, new IntPtr(NativeMethods.SC_MONITORPOWER), new IntPtr(2));
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"System command execution failed: {ex.Message}");
            }
        });
    }

    private void RefreshGreeting()
    {
        int hour = DateTime.Now.Hour;
        string greeting = hour < 12 ? "Good morning"
                        : hour < 17 ? "Good afternoon"
                        : "Good evening";
        GreetingText.Text = greeting;
        GreetingSubText.Text = $"{DateTime.Now:dddd, MMMM d} - search apps, calculate, or run a command.";
        RefreshDashboardStats();
    }

    private void RefreshDashboardStats()
    {
        DashboardAppCountText.Text = _apps.Count > 0 ? _apps.Count.ToString() : "--";
        DashboardStatusText.Text = _isScanning
            ? "Indexing apps"
            : _apps.Count > 0
                ? "Ready"
                : "Waiting for index";

        int recentCount = _recentEntries.Count;
        RecentSummaryText.Text = recentCount == 0
            ? "No launches yet"
            : $"{Math.Min(recentCount, 6)} shown";
    }

    private void RefreshRecentItems()
    {
        RecentAppsContainer.Children.Clear();
        RefreshDashboardStats();

        if (_recentEntries.Count == 0)
        {
            RecentAppsSection.Visibility = Visibility.Collapsed;
            RecentEmptyState.Visibility = Visibility.Visible;
            return;
        }
        RecentAppsSection.Visibility = Visibility.Visible;
        RecentEmptyState.Visibility = Visibility.Collapsed;

        foreach (var entry in _recentEntries.Take(6))
        {
            AppItem? match = _apps.FirstOrDefault(a =>
                (!string.IsNullOrEmpty(entry.AUMID) && a.AUMID == entry.AUMID) ||
                (!string.IsNullOrEmpty(entry.Path) && a.Path == entry.Path));

            // Create a button row for each recent app
            var btn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SurfaceCard"],
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SearchBoxBorder"],
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 8, 10, 8),
                CornerRadius = new CornerRadius(9)
            };

            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Icon container
            var iconBorder = new Border
            {
                Width = 36, Height = 36,
                CornerRadius = new CornerRadius(8),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x1A, 0x1A, 0x24))
            };
            if (match?.CachedIconSource != null)
            {
                iconBorder.Child = new Image
                {
                    Source = match.CachedIconSource,
                    Width = 26, Height = 26,
                    Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform
                };
            }
            else
            {
                iconBorder.Child = new FontIcon
                {
                    Glyph = "\uE71D",
                    FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["IconFont"],
                    FontSize = 16,
                    Foreground = (Microsoft.UI.Xaml.Media.SolidColorBrush)Application.Current.Resources["TextMuted"]
                };
            }
            Grid.SetColumn(iconBorder, 0);
            grid.Children.Add(iconBorder);

            // Text stack
            var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
            textStack.Children.Add(new TextBlock
            {
                Text = entry.Name,
                FontSize = 13.5,
                FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["DisplayFont"],
                FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextPrimary"],
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            textStack.Children.Add(new TextBlock
            {
                Text = entry.IsUWP ? "Store app" : "Desktop app",
                FontSize = 10,
                FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["BodyFont"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextMuted"]
            });
            Grid.SetColumn(textStack, 1);
            grid.Children.Add(textStack);

            // Recent icon
            var recentIcon = new FontIcon
            {
                Glyph = "\uE8E5",
                FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["IconFont"],
                FontSize = 12,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextMuted"],
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(recentIcon, 2);
            grid.Children.Add(recentIcon);

            btn.Content = grid;

            var capturedEntry = entry;
            btn.Click += (s, e) =>
            {
                ToggleVisibility(false);
                _ = Task.Run(() => AppScanner.Launch(new AppItem
                {
                    Name = capturedEntry.Name,
                    Path = capturedEntry.Path,
                    IsUWP = capturedEntry.IsUWP,
                    AUMID = capturedEntry.AUMID
                }));
            };

            RecentAppsContainer.Children.Add(btn);
        }
    }

    private void SeedSearch(string text, bool selectAll = true)
    {
        SearchBox.Text = text;
        SearchBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
        if (selectAll)
            SearchBox.SelectAll();
        else
            SearchBox.Select(SearchBox.Text.Length, 0);
    }


    private void QuickCalcButton_Click(object sender, RoutedEventArgs e)
    {
        SeedSearch("2 + 2");
    }

    private void QuickBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        SeedSearch(" ", selectAll: false);
    }

    private void QuickShellButton_Click(object sender, RoutedEventArgs e)
    {
        SeedSearch("> ", selectAll: false);
    }

    private void QuickHelpButton_Click(object sender, RoutedEventArgs e)
    {
        SeedSearch("?");
    }

    private void ExampleNotepadButton_Click(object sender, RoutedEventArgs e)
    {
        SeedSearch("notepad");
    }

    private void ExampleCalcButton_Click(object sender, RoutedEventArgs e)
    {
        SeedSearch("(15 + 25) * 4");
    }

    private void ExampleShellButton_Click(object sender, RoutedEventArgs e)
    {
        SeedSearch("> ipconfig");
    }

    // ═══════════════════════════════════════════════════
    //  Launch Actions
    // ═══════════════════════════════════════════════════

    private void LaunchSelected()
    {
        int index = ResultsListView.SelectedIndex;
        if (index < 0 || index >= _searchResults.Count) return;

        var selected = _searchResults[index];
        if (selected.IsHeader) return;

        if (selected.IsCalculator)
        {
            ToggleVisibility(false);
            CopySelectedPathOrResult();
            return;
        }

        if (selected.IsHelp)
        {
            ToggleVisibility(false);
            CopySelectedPathOrResult();
            return;
        }

        if (selected.IsWebUrl)
        {
            ToggleVisibility(false);
            _ = Task.Run(() =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(selected.WebUrl) { UseShellExecute = true });
                }
                catch (Exception ex) { Debug.WriteLine($"Web URL launch failed: {ex.Message}"); }
            });
            return;
        }

        if (selected.IsWebSearch)
        {
            ToggleVisibility(false);
            _ = Task.Run(() =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(selected.Path) { UseShellExecute = true });
                }
                catch (Exception ex) { Debug.WriteLine($"Web search launch failed: {ex.Message}"); }
            });
            return;
        }

        if (selected.IsSystemCommand)
        {
            _ = ExecuteSystemCommandAsync(selected);
            return;
        }

        if (selected.IsShellCommand)
        {
            if (!string.IsNullOrEmpty(selected.ShellCommandText))
            {
                ToggleVisibility(false);
                _ = Task.Run(() =>
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo("cmd.exe", $"/k \"{selected.ShellCommandText}\"") { UseShellExecute = true });
                    }
                    catch (Exception ex) { Debug.WriteLine($"Command launch failed: {ex.Message}"); }
                });
            }
            return;
        }

        ToggleVisibility(false);

        var appItem = new AppItem
        {
            Name = selected.Name,
            Path = selected.Path,
            IsUWP = selected.IsUWP,
            AUMID = selected.AUMID
        };

        // Track in recents
        _recentEntries = RecentAppsService.Push(_recentEntries, appItem);

        _ = Task.Run(() => AppScanner.Launch(appItem));
    }

    private void LaunchSelectedAsAdmin()
    {
        int index = ResultsListView.SelectedIndex;
        if (index < 0 || index >= _searchResults.Count) return;
        var selected = _searchResults[index];
        if (selected.IsCalculator || selected.IsHelp || selected.IsHeader || selected.IsSystemCommand || selected.IsWebUrl || selected.IsWebSearch) return;

        if (selected.IsShellCommand)
        {
            if (!string.IsNullOrEmpty(selected.ShellCommandText))
            {
                ToggleVisibility(false);
                _ = Task.Run(() =>
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo("cmd.exe", $"/k \"{selected.ShellCommandText}\"")
                        {
                            UseShellExecute = true,
                            Verb = "runas"
                        });
                    }
                    catch (Exception ex) { Debug.WriteLine($"Run command as admin failed: {ex.Message}"); }
                });
            }
            return;
        }

        if (selected.IsUWP) return;

        ToggleVisibility(false);
        _ = Task.Run(() =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(selected.Path)
                {
                    UseShellExecute = true,
                    Verb = "runas"
                });
            }
            catch (Exception ex) { Debug.WriteLine($"Run as admin failed: {ex.Message}"); }
        });
    }

    private void OpenSelectedLocation()
    {
        int index = ResultsListView.SelectedIndex;
        if (index < 0 || index >= _searchResults.Count) return;
        var selected = _searchResults[index];
        if (selected.IsCalculator || selected.IsShellCommand || selected.IsHelp || selected.IsUWP || string.IsNullOrEmpty(selected.Path) || selected.IsHeader || selected.IsSystemCommand || selected.IsWebUrl || selected.IsWebSearch) return;
        ToggleVisibility(false);
        _ = Task.Run(() =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{selected.Path}\"")
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex) { Debug.WriteLine($"Open location failed: {ex.Message}"); }
        });
    }

    private void CopySelectedPathOrResult()
    {
        int index = ResultsListView.SelectedIndex;
        if (index < 0 || index >= _searchResults.Count) return;
        var selected = _searchResults[index];
        if (selected.IsHeader) return;

        string textToCopy = selected.IsCalculator ? selected.CalcResult
                          : (selected.IsShellCommand ? selected.ShellCommandText
                          : (selected.IsHelp ? $"{selected.Name}: {selected.Path}\n{selected.HelpDetail}"
                          : (selected.IsWebUrl ? selected.WebUrl
                          : (selected.IsWebSearch ? selected.Path
                          : (selected.IsUWP ? selected.AUMID : selected.Path)))));
        if (string.IsNullOrEmpty(textToCopy)) return;

        var dataPackage = new DataPackage();
        dataPackage.SetText(textToCopy);
        Clipboard.SetContent(dataPackage);

        var prev = FooterStatusText.Text;
        FooterStatusText.Text = "✓ Copied to clipboard";
        _ = Task.Delay(1800).ContinueWith(_ =>
            DispatcherQueue.TryEnqueue(() => FooterStatusText.Text = prev));
    }

    private static bool IsKeyPressed(int vk)
        => (NativeMethods.GetKeyState(vk) & 0x8000) != 0;

    private bool HandleGlobalKeys(Windows.System.VirtualKey key)
    {
        bool ctrl = IsKeyPressed(NativeMethods.VK_CONTROL);
        bool shift = IsKeyPressed(NativeMethods.VK_SHIFT);

        if (key == Windows.System.VirtualKey.Enter)
        {
            if (ctrl && shift) LaunchSelectedAsAdmin();
            else LaunchSelected();
            return true;
        }
        else if (key == Windows.System.VirtualKey.C && ctrl)
        {
            CopySelectedPathOrResult();
            return true;
        }
        else if (key == Windows.System.VirtualKey.O && ctrl && shift)
        {
            OpenSelectedLocation();
            return true;
        }
        return false;
    }

    private void ToggleVisibility(bool show)
    {
        if (show)
        {
            _isVisible = true;
            WindowHelper.CenterWindow(_hwnd, 800, 530);
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOW);
            WindowHelper.ForceForeground(_hwnd);
            SearchBox.Text = string.Empty;
            SearchBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            // Reset to dashboard
            _isDashboardVisible = false; // force re-show
            RefreshGreeting();
            RefreshRecentItems();
            SetPreviewPaneVisibility(false);
            ShowDashboard(animate: false);
        }
        else
        {
            _isVisible = false;
            ShowSettings(false);
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        => UpdateSearch(SearchBox.Text);

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (HandleGlobalKeys(e.Key)) { e.Handled = true; return; }

        switch (e.Key)
        {
            case Windows.System.VirtualKey.Down:
                if (!_isDashboardVisible)
                {
                    // Move to next non-header
                    int next = ResultsListView.SelectedIndex + 1;
                    while (next < _searchResults.Count && _searchResults[next].IsHeader) next++;
                    if (next < _searchResults.Count)
                    {
                        ResultsListView.SelectedIndex = next;
                        ResultsListView.ScrollIntoView(ResultsListView.SelectedItem);
                    }
                }
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Up:
                if (!_isDashboardVisible)
                {
                    int prev = ResultsListView.SelectedIndex - 1;
                    while (prev >= 0 && _searchResults[prev].IsHeader) prev--;
                    if (prev >= 0)
                    {
                        ResultsListView.SelectedIndex = prev;
                        ResultsListView.ScrollIntoView(ResultsListView.SelectedItem);
                    }
                }
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Escape:
                ToggleVisibility(false);
                e.Handled = true;
                break;
        }
    }

    private void ResultsListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SearchResultItem item && !item.IsHeader)
            LaunchSelected();
    }

    private void ResultsListView_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (HandleGlobalKeys(e.Key)) { e.Handled = true; return; }
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            ToggleVisibility(false);
            e.Handled = true;
        }
    }

    // ═══════════════════════════════════════════════════
    //  System Tray
    // ═══════════════════════════════════════════════════

    private void AddTrayIcon()
    {
        try
        {
            IntPtr hIcon = IntPtr.Zero;

            string localIconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
            if (File.Exists(localIconPath))
            {
                hIcon = NativeMethods.LoadImageW(IntPtr.Zero, localIconPath,
                    NativeMethods.IMAGE_ICON, 32, 32, NativeMethods.LR_LOADFROMFILE);
            }

            if (hIcon == IntPtr.Zero)
            {
                string exePath = Environment.ProcessPath ?? string.Empty;
                if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                    NativeMethods.ExtractIconExW(exePath, 0, out hIcon, out _, 1);
            }

            if (hIcon == IntPtr.Zero)
            {
                string exePath = Environment.ProcessPath ?? string.Empty;
                var sfi = new NativeMethods.SHFILEINFOW();
                NativeMethods.SHGetFileInfoW(exePath, 0, ref sfi, (uint)Marshal.SizeOf(sfi),
                    NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON);
                hIcon = sfi.hIcon;
            }

            var data = new NativeMethods.NOTIFYICONDATAW
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
                hWnd = _hwnd,
                uID = 1,
                uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
                uCallbackMessage = NativeMethods.WM_TRAYICON,
                hIcon = hIcon,
                szTip = "WinCast"
            };

            NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_ADD, ref data);
            if (hIcon != IntPtr.Zero) NativeMethods.DestroyIcon(hIcon);
        }
        catch (Exception ex) { Debug.WriteLine($"AddTrayIcon failed: {ex.Message}"); }
    }

    private void RemoveTrayIcon()
    {
        try
        {
            var data = new NativeMethods.NOTIFYICONDATAW
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
                hWnd = _hwnd,
                uID = 1
            };
            NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_DELETE, ref data);
        }
        catch { /* non-critical */ }
    }

    private void ShowTrayContextMenu()
    {
        IntPtr hMenu = NativeMethods.CreatePopupMenu();
        if (hMenu == IntPtr.Zero) return;
        try
        {
            string toggleText = _isVisible ? "Hide WinCast" : "Show WinCast";
            NativeMethods.AppendMenuW(hMenu, NativeMethods.MF_STRING, 1001, toggleText);
            NativeMethods.AppendMenuW(hMenu, NativeMethods.MF_STRING, 1003, "Settings");
            NativeMethods.AppendMenuW(hMenu, NativeMethods.MF_SEPARATOR, 0, string.Empty);
            NativeMethods.AppendMenuW(hMenu, NativeMethods.MF_STRING, 1002, "Exit");

            NativeMethods.GetCursorPos(out var pt);
            NativeMethods.SetForegroundWindow(_hwnd);

            int command = NativeMethods.TrackPopupMenu(
                hMenu, NativeMethods.TPM_LEFTALIGN | NativeMethods.TPM_RETURNCMD,
                pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);

            if (command == 1001) ToggleVisibility(!_isVisible);
            else if (command == 1003) OpenSettings();
            else if (command == 1002) this.Close();
        }
        finally { NativeMethods.DestroyMenu(hMenu); }
    }

    private void OpenSettings()
    {
        ToggleVisibility(true);
        ShowSettings(true);
    }

    // ═══════════════════════════════════════════════════
    //  Settings & Preview Visibility Control
    // ═══════════════════════════════════════════════════

    private void SetPreviewPaneVisibility(bool show)
    {
        if (show && SettingsService.Instance.ShowPreview)
        {
            DetailColumn.Width = new GridLength(285);
            VerticalDivider.Visibility = Visibility.Visible;
            DetailPanel.Visibility = Visibility.Visible;
        }
        else
        {
            DetailColumn.Width = new GridLength(0);
            VerticalDivider.Visibility = Visibility.Collapsed;
            DetailPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSettings(true);
    }

    private void BackToMainButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSettings(false);
    }

    private void ShowSettings(bool show)
    {
        _isSettingsVisible = show;
        if (show)
        {
            _searchCts?.Cancel();
            SetPreviewPaneVisibility(false);
            DashboardPanel.Visibility = Visibility.Collapsed;
            DashboardPanel.Opacity = 0;
            ResultsPanel.Visibility = Visibility.Collapsed;
            ResultsPanel.Opacity = 0;
            _isDashboardVisible = false;
            SearchBox.Text = string.Empty;

            ShowPreviewToggle.IsOn = SettingsService.Instance.ShowPreview;
            LaunchOnStartupToggle.IsOn = SettingsService.Instance.LaunchOnStartup;
            CurrentVersionText.Text = $"Current version {UpdateService.CurrentVersion}";
            UpdateStatusText.Text = "Check GitHub Releases for a newer installer.";
            DownloadUpdateButton.Visibility = _availableUpdate?.IsUpdateAvailable == true
                ? Visibility.Visible
                : Visibility.Collapsed;

            ThemeModeComboBox.SelectedIndex = SettingsService.Instance.ThemeMode switch
            {
                "System" => 0,
                "Dark" => 1,
                "Light" => 2,
                _ => 1
            };

            BackdropComboBox.SelectedIndex = SettingsService.Instance.BackdropType switch
            {
                "Mica" => 0,
                "Acrylic" => 1,
                "None" or "Solid" => 2,
                _ => 0
            };

            SurfaceOpacityComboBox.SelectedIndex = SettingsService.Instance.SurfaceOpacity switch
            {
                "Subtle" => 0,
                "Balanced" => 1,
                "Glass" => 2,
                _ => 1
            };

            SwitchSettingsTab(true);
            SettingsPanel.Visibility = Visibility.Visible;
            SearchBox.IsEnabled = false;
        }
        else
        {
            SettingsPanel.Visibility = Visibility.Collapsed;
            SearchBox.IsEnabled = true;
            SearchBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            _isDashboardVisible = false;
            ShowDashboard(animate: false);
        }
    }

    private void SwitchSettingsTab(bool showSettings)
    {
        SettingsScrollViewer.Visibility = showSettings ? Visibility.Visible : Visibility.Collapsed;
        HelpScrollViewer.Visibility = showSettings ? Visibility.Collapsed : Visibility.Visible;

        SettingsTabButton.FontWeight = showSettings ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        SettingsTabButton.Foreground = (Microsoft.UI.Xaml.Media.Brush)App.Current.Resources[showSettings ? "TextPrimary" : "TextMuted"];

        HelpTabButton.FontWeight = showSettings ? Microsoft.UI.Text.FontWeights.Normal : Microsoft.UI.Text.FontWeights.SemiBold;
        HelpTabButton.Foreground = (Microsoft.UI.Xaml.Media.Brush)App.Current.Resources[showSettings ? "TextMuted" : "TextPrimary"];
    }

    private void ShowPreviewToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (ShowPreviewToggle == null) return;
        SettingsService.Instance.ShowPreview = ShowPreviewToggle.IsOn;
        SettingsService.Save();

        // Re-evaluate preview visibility based on current state
        if (!_isDashboardVisible && ResultsListView.SelectedItem != null)
            SetPreviewPaneVisibility(ShowPreviewToggle.IsOn);
    }

    private void LaunchOnStartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (LaunchOnStartupToggle == null) return;
        SettingsService.Instance.LaunchOnStartup = LaunchOnStartupToggle.IsOn;
        SettingsService.Save();
    }

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        DownloadUpdateButton.Visibility = Visibility.Collapsed;
        UpdateStatusText.Text = "Checking GitHub Releases...";

        try
        {
            _availableUpdate = await UpdateService.CheckForUpdatesAsync();

            if (_availableUpdate.IsUpdateAvailable)
            {
                UpdateStatusText.Text = $"Version {_availableUpdate.TagName} is available.";
                SetUpdateAvailable(_availableUpdate);
            }
            else
            {
                UpdateStatusText.Text = $"You're up to date on version {UpdateService.CurrentVersion}.";
                ClearAvailableUpdate();
            }
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"Update check failed: {ex.Message}";
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private async void DownloadUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        await DownloadAndInstallUpdateAsync();
    }

    private async void FooterUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        await DownloadAndInstallUpdateAsync();
    }

    private async Task CheckForUpdatesInBackgroundAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(5));

        try
        {
            var update = await UpdateService.CheckForUpdatesAsync();
            if (update.IsUpdateAvailable)
                SetUpdateAvailable(update);
        }
        catch
        {
            // Silent background check: the footer should stay quiet unless an update exists.
        }
    }

    private void SetUpdateAvailable(UpdateInfo update)
    {
        _availableUpdate = update;
        FooterUpdateButton.Visibility = Visibility.Visible;
        FooterUpdateIcon.Visibility = Visibility.Visible;
        FooterUpdateRing.Visibility = Visibility.Collapsed;
        FooterUpdateRing.IsActive = false;
        FooterUpdateButton.IsEnabled = true;
        DownloadUpdateButton.Visibility = Visibility.Visible;
        DownloadUpdateButton.IsEnabled = true;
        FooterStatusText.Text = $"Update {update.TagName} available";
    }

    private void ClearAvailableUpdate()
    {
        _availableUpdate = null;
        FooterUpdateButton.Visibility = Visibility.Collapsed;
        DownloadUpdateButton.Visibility = Visibility.Collapsed;
    }

    private async Task DownloadAndInstallUpdateAsync()
    {
        if (_availableUpdate == null || _isUpdateDownloading) return;

        _isUpdateDownloading = true;
        DownloadUpdateButton.IsEnabled = false;
        CheckUpdatesButton.IsEnabled = false;
        FooterUpdateButton.IsEnabled = false;
        FooterUpdateIcon.Visibility = Visibility.Collapsed;
        FooterUpdateRing.Visibility = Visibility.Visible;
        FooterUpdateRing.IsActive = true;
        UpdateStatusText.Text = $"Downloading {_availableUpdate.InstallerName}...";
        FooterStatusText.Text = "Downloading update...";

        try
        {
            var progress = new Progress<double>(value =>
            {
                int percent = Math.Clamp((int)Math.Round(value * 100), 0, 100);
                UpdateStatusText.Text = $"Downloading update... {percent}%";
                FooterStatusText.Text = $"Update {percent}%";
            });

            string installerPath = await UpdateService.DownloadInstallerAsync(_availableUpdate, progress);
            UpdateStatusText.Text = "Starting installer...";
            FooterStatusText.Text = "Starting installer...";
            UpdateService.StartInstallerAndExit(installerPath);
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"Download failed: {ex.Message}";
            FooterStatusText.Text = "Update download failed";
            DownloadUpdateButton.IsEnabled = true;
            CheckUpdatesButton.IsEnabled = true;
            FooterUpdateButton.IsEnabled = true;
            FooterUpdateIcon.Visibility = Visibility.Visible;
            FooterUpdateRing.Visibility = Visibility.Collapsed;
            FooterUpdateRing.IsActive = false;
            _isUpdateDownloading = false;
        }
    }

    private void ThemeModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeModeComboBox == null) return;
        string val = (ThemeModeComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Dark";

        if (SettingsService.Instance.ThemeMode != val)
        {
            SettingsService.Instance.ThemeMode = val;
            SettingsService.Save();
            ApplyVisualTheme();
        }
    }

    private void BackdropComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BackdropComboBox == null) return;
        string val = (BackdropComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Mica";
        if (val == "Solid") val = "None";

        if (SettingsService.Instance.BackdropType != val)
        {
            SettingsService.Instance.BackdropType = val;
            SettingsService.Save();
            ApplyVisualTheme();
        }
    }

    private void SurfaceOpacityComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SurfaceOpacityComboBox == null) return;
        string val = (SurfaceOpacityComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Balanced";

        if (SettingsService.Instance.SurfaceOpacity != val)
        {
            SettingsService.Instance.SurfaceOpacity = val;
            SettingsService.Save();
            ApplyVisualTheme();
        }
    }

    private void ApplyVisualTheme()
    {
        var settings = SettingsService.Instance;

        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = settings.ThemeMode switch
            {
                "Light" => ElementTheme.Light,
                "System" => ElementTheme.Default,
                _ => ElementTheme.Dark
            };
        }

        if (settings.BackdropType == "Acrylic")
            WindowHelper.ApplyBackdrop(this, useAcrylic: true);
        else if (settings.BackdropType == "Mica")
            WindowHelper.ApplyBackdrop(this, useAcrylic: false);
        else
            SystemBackdrop = null;

        bool light = settings.ThemeMode == "Light";
        byte surfaceAlpha = settings.SurfaceOpacity switch
        {
            "Subtle" => (byte)0xF2,
            "Glass" => (byte)0xB8,
            _ => (byte)0xD8
        };
        byte cardAlpha = settings.SurfaceOpacity switch
        {
            "Subtle" => (byte)0xFA,
            "Glass" => (byte)0xCC,
            _ => (byte)0xE8
        };
        byte footerAlpha = settings.SurfaceOpacity switch
        {
            "Subtle" => (byte)0xF5,
            "Glass" => (byte)0xC8,
            _ => (byte)0xE0
        };

        if (settings.BackdropType == "None")
        {
            surfaceAlpha = 0xFF;
            cardAlpha = 0xFF;
            footerAlpha = 0xFF;
        }

        if (light)
            ApplyPalette(
                app: ColorBrush(surfaceAlpha, 0xF4, 0xF4, 0xF7),
                card: ColorBrush(cardAlpha, 0xFF, 0xFF, 0xFF),
                hover: ColorBrush(0xFF, 0xEA, 0xEA, 0xEF),
                selected: ColorBrush(0xFF, 0xDD, 0xDD, 0xE8),
                border: ColorBrush(0x88, 0xBA, 0xBA, 0xC6),
                divider: ColorBrush(0x7A, 0xCC, 0xCC, 0xD6),
                detail: ColorBrush(footerAlpha, 0xEE, 0xEE, 0xF3),
                primary: ColorBrush(0xFF, 0x18, 0x18, 0x22),
                secondary: ColorBrush(0xFF, 0x5E, 0x5E, 0x75),
                muted: ColorBrush(0xFF, 0x8A, 0x8A, 0xA0));
        else
            ApplyPalette(
                app: ColorBrush(surfaceAlpha, 0x1C, 0x1C, 0x1C),
                card: ColorBrush(cardAlpha, 0x28, 0x28, 0x28),
                hover: ColorBrush(0xFF, 0x32, 0x32, 0x32),
                selected: ColorBrush(0xFF, 0x3C, 0x3C, 0x3C),
                border: ColorBrush(0xAA, 0x30, 0x30, 0x30),
                divider: ColorBrush(0x90, 0x2A, 0x2A, 0x2A),
                detail: ColorBrush(footerAlpha, 0x18, 0x18, 0x18),
                primary: ColorBrush(0xFF, 0xF0, 0xF0, 0xFA),
                secondary: ColorBrush(0xFF, 0x88, 0x88, 0xAA),
                muted: ColorBrush(0xFF, 0x55, 0x55, 0x6A));

        RootSurface.Background = CreateRootSurfaceBrush(settings, light, surfaceAlpha);
        RootSurface.BorderBrush = (Brush)App.Current.Resources["SearchBoxBorder"];
        FooterStatusText.Text = "Theme applied";
    }

    private static SolidColorBrush ColorBrush(byte a, byte r, byte g, byte b)
        => new(Microsoft.UI.ColorHelper.FromArgb(a, r, g, b));

    private static Brush CreateRootSurfaceBrush(AppSettings settings, bool light, byte surfaceAlpha)
    {
        if (settings.BackdropType == "Acrylic")
        {
            double tintOpacity = settings.SurfaceOpacity switch
            {
                "Subtle" => 0.82,
                "Glass" => 0.36,
                _ => 0.58
            };

            return new AcrylicBrush
            {
                TintColor = light
                    ? Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xF6, 0xF6, 0xFA)
                    : Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x18, 0x18, 0x1F),
                TintOpacity = tintOpacity,
                FallbackColor = light
                    ? Microsoft.UI.ColorHelper.FromArgb(surfaceAlpha, 0xF4, 0xF4, 0xF7)
                    : Microsoft.UI.ColorHelper.FromArgb(surfaceAlpha, 0x1C, 0x1C, 0x1C)
            };
        }

        return (Brush)App.Current.Resources["AppBackground"];
    }

    private static void ApplyPalette(
        SolidColorBrush app,
        SolidColorBrush card,
        SolidColorBrush hover,
        SolidColorBrush selected,
        SolidColorBrush border,
        SolidColorBrush divider,
        SolidColorBrush detail,
        SolidColorBrush primary,
        SolidColorBrush secondary,
        SolidColorBrush muted)
    {
        SetBrushResource("AppBackground", app);
        SetBrushResource("SurfaceBase", app);
        SetBrushResource("SurfaceCard", card);
        SetBrushResource("SurfaceHover", hover);
        SetBrushResource("SurfaceSelected", selected);
        SetBrushResource("SearchBoxBorder", border);
        SetBrushResource("DividerBrush", divider);
        SetBrushResource("DetailPaneBackground", detail);
        SetBrushResource("SearchBoxBackground", app);
        SetBrushResource("ItemHoverBackground", hover);
        SetBrushResource("ItemActiveBackground", selected);
        SetBrushResource("TextPrimary", primary);
        SetBrushResource("TextSecondary", secondary);
        SetBrushResource("TextMuted", muted);
    }

    private static void SetBrushResource(string key, SolidColorBrush brush)
    {
        if (App.Current.Resources[key] is SolidColorBrush existing)
            existing.Color = brush.Color;
        else
            App.Current.Resources[key] = brush;
    }

}
