using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
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

    public MainWindow()
    {
        InitializeComponent();

        _hwnd = WindowHelper.GetHWND(this);
        WindowHelper.MakeBorderless(_hwnd);
        WindowHelper.CenterWindow(_hwnd, 800, 530);

        // Apply system backdrop from settings
        string backdrop = SettingsService.Instance.BackdropType;
        if (backdrop == "Acrylic")
            WindowHelper.ApplyBackdrop(this, useAcrylic: true);
        else if (backdrop == "Mica")
            WindowHelper.ApplyBackdrop(this, useAcrylic: false);
        else
            this.SystemBackdrop = null;

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
        BackdropComboBox.SelectionChanged += BackdropComboBox_SelectionChanged;
        SettingsTabButton.Click += (s, e) => SwitchSettingsTab(true);
        HelpTabButton.Click += (s, e) => SwitchSettingsTab(false);
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
        if (_isDashboardVisible) return;
        _isDashboardVisible = true;
        SetPreviewPaneVisibility(false);

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
            string category = res.IsCalculator ? "Calculator" : (res.IsShellCommand ? "Command" : "Applications");

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
                res.ShellCommandText));
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

        if (selectedItem.IsCalculator)
        {
            DetailCalcIcon.Visibility = Visibility.Visible;
            DetailShellIcon.Visibility = Visibility.Collapsed;
            DetailAppIcon.Visibility = Visibility.Collapsed;
            DetailTitleText.Text = "Calculator";
            TypeBadgeText.Text = "Calculator";
            TypeBadge.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x4A, 0x3F, 0xAA));

            DetailPathContainer.Visibility = Visibility.Collapsed;
            DetailAumidContainer.Visibility = Visibility.Collapsed;
            DetailCommandContainer.Visibility = Visibility.Collapsed;
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
            DetailCalcIcon.Visibility = Visibility.Collapsed;
            DetailShellIcon.Visibility = Visibility.Visible;
            DetailAppIcon.Visibility = Visibility.Collapsed;
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

            ActionOpenGrid.Visibility = Visibility.Visible;
            ActionOpenText.Text = "Execute Command";
            ActionCopyResultGrid.Visibility = Visibility.Collapsed;
            ActionAdminGrid.Visibility = Visibility.Visible;
            ActionLocationGrid.Visibility = Visibility.Collapsed;
            ActionCopyPathGrid.Visibility = Visibility.Visible;
            ActionCopyPathText.Text = "Copy Command";
        }
        else
        {
            DetailCalcIcon.Visibility = Visibility.Collapsed;
            DetailShellIcon.Visibility = Visibility.Collapsed;
            DetailAppIcon.Visibility = Visibility.Visible;
            DetailAppIcon.Source = selectedItem.IconSource;
            DetailTitleText.Text = selectedItem.Name;

            DetailEquationContainer.Visibility = Visibility.Collapsed;
            DetailResultContainer.Visibility = Visibility.Collapsed;
            DetailCommandContainer.Visibility = Visibility.Collapsed;

            if (selectedItem.IsUWP)
            {
                TypeBadgeText.Text = "UWP";
                TypeBadge.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x0E, 0x74, 0x90));
                // UWP app — no subtitle text element in XAML
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
                // Win32 app — no subtitle text element in XAML
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

    // ═══════════════════════════════════════════════════
    //  Recent Apps Dashboard
    // ═══════════════════════════════════════════════════

    private void RefreshGreeting()
    {
        int hour = DateTime.Now.Hour;
        string greeting = hour < 12 ? "Good morning ☀️"
                        : hour < 17 ? "Good afternoon 🌤"
                        : "Good evening 🌙";
        GreetingText.Text = greeting;
        GreetingSubText.Text = DateTime.Now.ToString("dddd, MMMM d");
    }

    private void RefreshRecentItems()
    {
        RecentAppsContainer.Children.Clear();

        if (_recentEntries.Count == 0)
        {
            RecentAppsSection.Visibility = Visibility.Collapsed;
            return;
        }
        RecentAppsSection.Visibility = Visibility.Visible;

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
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8, 7, 8, 7),
                CornerRadius = new CornerRadius(8)
            };

            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Icon container
            var iconBorder = new Border
            {
                Width = 32, Height = 32,
                CornerRadius = new CornerRadius(7),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x1A, 0x1A, 0x24))
            };
            if (match?.CachedIconSource != null)
            {
                iconBorder.Child = new Image
                {
                    Source = match.CachedIconSource,
                    Width = 24, Height = 24,
                    Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform
                };
            }
            else
            {
                iconBorder.Child = new FontIcon
                {
                    Glyph = "\uE71D",
                    FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["IconFont"],
                    FontSize = 14,
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
                FontSize = 13,
                FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["DisplayFont"],
                FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextPrimary"],
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            textStack.Children.Add(new TextBlock
            {
                Text = entry.IsUWP ? "UWP" : "Win32",
                FontSize = 10,
                FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["BodyFont"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextMuted"]
            });
            Grid.SetColumn(textStack, 1);
            grid.Children.Add(textStack);

            // Recent icon
            var recentIcon = new FontIcon
            {
                Glyph = "\uE81C",
                FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["IconFont"],
                FontSize = 11,
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


    private void QuickCalcButton_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "2 + 2";
        SearchBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
        SearchBox.SelectAll();
    }

    private void QuickBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = " ";
        SearchBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
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

        ToggleVisibility(false);

        if (selected.IsCalculator)
        {
            CopySelectedPathOrResult();
            return;
        }

        if (selected.IsShellCommand)
        {
            if (!string.IsNullOrEmpty(selected.ShellCommandText))
            {
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
        if (selected.IsCalculator || selected.IsHeader) return;

        ToggleVisibility(false);

        if (selected.IsShellCommand)
        {
            if (!string.IsNullOrEmpty(selected.ShellCommandText))
            {
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
        if (selected.IsCalculator || selected.IsShellCommand || selected.IsUWP || string.IsNullOrEmpty(selected.Path) || selected.IsHeader) return;
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
                          : (selected.IsUWP ? selected.AUMID : selected.Path));
        if (string.IsNullOrEmpty(textToCopy)) return;

        var dataPackage = new DataPackage();
        dataPackage.SetText(textToCopy);
        Clipboard.SetContent(dataPackage);

        var prev = FooterStatusText.Text;
        FooterStatusText.Text = "✓ Copied to clipboard";
        _ = Task.Delay(1800).ContinueWith(_ =>
            DispatcherQueue.TryEnqueue(() => FooterStatusText.Text = prev));
    }

    // ═══════════════════════════════════════════════════
    //  Keyboard Handling
    // ═══════════════════════════════════════════════════

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

            ShowPreviewToggle.IsOn = SettingsService.Instance.ShowPreview;
            LaunchOnStartupToggle.IsOn = SettingsService.Instance.LaunchOnStartup;

            BackdropComboBox.SelectedIndex = SettingsService.Instance.BackdropType switch
            {
                "Mica" => 0,
                "Acrylic" => 1,
                "None" => 2,
                _ => 0
            };

            SwitchSettingsTab(true);
            SettingsPanel.Visibility = Visibility.Visible;
            SearchBox.IsEnabled = false; // Disable search while in settings
        }
        else
        {
            SettingsPanel.Visibility = Visibility.Collapsed;
            SearchBox.IsEnabled = true;
            SearchBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            UpdateSearch(SearchBox.Text);
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

    private void BackdropComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BackdropComboBox == null) return;
        string val = (BackdropComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Mica";
        if (val == "Solid (None)") val = "None";

        if (SettingsService.Instance.BackdropType != val)
        {
            SettingsService.Instance.BackdropType = val;
            SettingsService.Save();
            FooterStatusText.Text = "Restart app to apply backdrop";
        }
    }

}
