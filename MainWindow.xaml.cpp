#include "pch.h"
#include "MainWindow.xaml.h"
#if __has_include("MainWindow.g.cpp")
#include "MainWindow.g.cpp"
#endif
#include "MicaHelper.h"
#include <commctrl.h>
#include <winrt/Windows.ApplicationModel.DataTransfer.h>

using namespace winrt;
using namespace winrt::Microsoft::UI::Xaml;
using namespace winrt::Microsoft::UI::Xaml::Input;
using namespace winrt::Microsoft::UI::Xaml::Controls;

namespace winrt::wincast::implementation
{
    // SearchResultItem Implementation
    SearchResultItem::SearchResultItem(
        hstring const& name, 
        hstring const& path, 
        bool isUwp, 
        bool isCalculator, 
        hstring const& calcResult, 
        winrt::Microsoft::UI::Xaml::Media::Imaging::SoftwareBitmapSource const& iconSource)
        : m_name(name), m_path(path), m_isUwp(isUwp), m_isCalculator(isCalculator), m_calcResult(calcResult), m_iconSource(iconSource)
    {
    }

    hstring SearchResultItem::Name() { return m_name; }
    hstring SearchResultItem::Path() { return m_path; }
    bool SearchResultItem::IsUWP() { return m_isUwp; }
    bool SearchResultItem::IsCalculator() { return m_isCalculator; }
    hstring SearchResultItem::CalcResult() { return m_calcResult; }
    winrt::Microsoft::UI::Xaml::Media::Imaging::SoftwareBitmapSource SearchResultItem::IconSource() { return m_iconSource; }

    winrt::Microsoft::UI::Xaml::Visibility SearchResultItem::GetCalcVisibility()
    {
        return m_isCalculator ? winrt::Microsoft::UI::Xaml::Visibility::Visible : winrt::Microsoft::UI::Xaml::Visibility::Collapsed;
    }

    winrt::Microsoft::UI::Xaml::Visibility SearchResultItem::GetUwpVisibility()
    {
        return m_isCalculator ? winrt::Microsoft::UI::Xaml::Visibility::Collapsed : winrt::Microsoft::UI::Xaml::Visibility::Visible;
    }

    // MainWindow Implementation
    MainWindow::MainWindow()
    {
        InitializeComponent();

        m_hwnd = ::wincast::utils::WindowHelper::GetHWND(*this);
        ::wincast::utils::WindowHelper::MakeBorderless(m_hwnd);
        ::wincast::utils::WindowHelper::CenterWindow(m_hwnd, 750, 480);
        ::wincast::utils::WindowHelper::ApplyBackdrop(*this, false); // Mica

        m_dispatcherQueue = winrt::Microsoft::UI::Dispatching::DispatcherQueue::GetForCurrentThread();
        m_searchResults = winrt::single_threaded_observable_vector<winrt::wincast::SearchResultItem>();
        ResultsListView().ItemsSource(m_searchResults);

        // Register Global Hotkey: Alt + Space
        RegisterHotKey(m_hwnd, 1, MOD_ALT, VK_SPACE);

        // Set subclass to intercept window messages (WndProc)
        SetWindowSubclass(m_hwnd, SubclassProc, 1, reinterpret_cast<DWORD_PTR>(this));

        // Start scanning applications in the background
        StartAppScan();

        // Initial focus
        SearchBox().Focus(winrt::Microsoft::UI::Xaml::FocusState::Programmatic);
    }

    MainWindow::~MainWindow()
    {
        if (m_hwnd)
        {
            UnregisterHotKey(m_hwnd, 1);
            RemoveWindowSubclass(m_hwnd, SubclassProc, 1);
        }
    }

    void MainWindow::StartAppScan()
    {
        if (m_isScanning) return;
        m_isScanning = true;

        auto weakThis = get_weak();
        std::thread([weakThis]() {
            auto scannedApps = ::wincast::scanner::AppScanner::ScanApps();

            if (auto self = weakThis.get())
            {
                self->m_dispatcherQueue.TryEnqueue([self, apps = std::move(scannedApps)]() {
                    self->m_apps = std::move(apps);
                    self->FooterStatusText().Text(std::to_wstring(self->m_apps.size()) + L" apps indexed");
                    self->UpdateSearch(L"");
                    self->m_isScanning = false;
                });
            }
        }).detach();
    }

    void MainWindow::UpdateSearch(std::wstring const& query)
    {
        if (m_searchAction)
        {
            m_searchAction.Cancel();
        }
        m_searchAction = UpdateSearchAsync(query);
    }

    winrt::Windows::Foundation::IAsyncAction MainWindow::UpdateSearchAsync(std::wstring query)
    {
        auto weakThis = get_weak();
        
        // Hop to background thread for scoring
        co_await winrt::resume_background();

        if (auto self = weakThis.get())
        {
            auto rawResults = ::wincast::search::SearchEngine::Search(self->m_apps, query);

            // Hop back to UI thread for rendering
            co_await resume_dispatcher(self->m_dispatcherQueue);

            self->m_searchResults.Clear();

            // Display top 15 results
            int count = std::min(15, static_cast<int>(rawResults.size()));
            for (int i = 0; i < count; ++i)
            {
                auto const& res = rawResults[i];
                winrt::Microsoft::UI::Xaml::Media::Imaging::SoftwareBitmapSource iconSource{ nullptr };

                if (res.Item.Icon)
                {
                    iconSource = winrt::Microsoft::UI::Xaml::Media::Imaging::SoftwareBitmapSource();
                    co_await iconSource.SetBitmapAsync(res.Item.Icon);
                }

                auto item = winrt::make<SearchResultItem>(
                    winrt::hstring(res.Item.Name),
                    winrt::hstring(res.Item.Path),
                    res.Item.IsUWP,
                    res.IsCalculator,
                    winrt::hstring(res.CalcResult),
                    iconSource
                );
                self->m_searchResults.Append(item);
            }

            if (self->m_searchResults.Size() > 0)
            {
                self->ResultsListView().SelectedIndex(0);
            }
        }
    }

    void MainWindow::LaunchSelected()
    {
        int index = ResultsListView().SelectedIndex();
        if (index >= 0 && index < static_cast<int>(m_searchResults.Size()))
        {
            auto selectedItem = m_searchResults.GetAt(index);
            ToggleVisibility(false);

            if (selectedItem.IsCalculator())
            {
                // Copy math result to clipboard
                auto dataPackage = winrt::Windows::ApplicationModel::DataTransfer::DataPackage();
                dataPackage.SetText(selectedItem.CalcResult());
                winrt::Windows::ApplicationModel::DataTransfer::Clipboard::SetContent(dataPackage);
                return;
            }

            // Launch application in background
            std::thread([selectedItem]() {
                ::wincast::scanner::AppItem appItem;
                appItem.Name = selectedItem.Name().c_str();
                appItem.Path = selectedItem.Path().c_str();
                appItem.IsUWP = selectedItem.IsUWP();
                appItem.AUMID = selectedItem.Path().c_str(); // AUMID stored in Path
                ::wincast::scanner::AppScanner::Launch(appItem);
            }).detach();
        }
    }

    void MainWindow::ToggleVisibility(bool show)
    {
        if (show)
        {
            m_isVisible = true;
            // Center on active monitor and display
            ::wincast::utils::WindowHelper::CenterWindow(m_hwnd, 750, 480);
            ShowWindow(m_hwnd, SW_SHOW);
            ::wincast::utils::WindowHelper::ForceForeground(m_hwnd);
            
            // Clear input box
            SearchBox().Text(L"");
            SearchBox().Focus(winrt::Microsoft::UI::Xaml::FocusState::Programmatic);
        }
        else
        {
            m_isVisible = false;
            ShowWindow(m_hwnd, SW_HIDE);
        }
    }

    void MainWindow::SearchBox_TextChanged(winrt::Windows::Foundation::IInspectable const&, winrt::Microsoft::UI::Xaml::Controls::TextChangedEventArgs const&)
    {
        UpdateSearch(SearchBox().Text().c_str());
    }

    void MainWindow::SearchBox_KeyDown(winrt::Windows::Foundation::IInspectable const&, winrt::Microsoft::UI::Xaml::Input::KeyRoutedEventArgs const& e)
    {
        auto key = e.Key();
        
        if (key == winrt::Windows::System::VirtualKey::Down)
        {
            int index = ResultsListView().SelectedIndex();
            if (index < static_cast<int>(m_searchResults.Size()) - 1)
            {
                ResultsListView().SelectedIndex(index + 1);
                ResultsListView().ScrollIntoView(ResultsListView().SelectedItem());
            }
            e.Handled(true);
        }
        else if (key == winrt::Windows::System::VirtualKey::Up)
        {
            int index = ResultsListView().SelectedIndex();
            if (index > 0)
            {
                ResultsListView().SelectedIndex(index - 1);
                ResultsListView().ScrollIntoView(ResultsListView().SelectedItem());
            }
            e.Handled(true);
        }
        else if (key == winrt::Windows::System::VirtualKey::Enter)
        {
            LaunchSelected();
            e.Handled(true);
        }
        else if (key == winrt::Windows::System::VirtualKey::Escape)
        {
            ToggleVisibility(false);
            e.Handled(true);
        }
    }

    void MainWindow::ResultsListView_ItemClick(winrt::Windows::Foundation::IInspectable const&, winrt::Microsoft::UI::Xaml::Controls::ItemClickEventArgs const&)
    {
        LaunchSelected();
    }

    void MainWindow::ResultsListView_KeyDown(winrt::Windows::Foundation::IInspectable const&, winrt::Microsoft::UI::Xaml::Input::KeyRoutedEventArgs const& e)
    {
        if (e.Key() == winrt::Windows::System::VirtualKey::Enter)
        {
            LaunchSelected();
            e.Handled(true);
        }
    }

    LRESULT CALLBACK MainWindow::SubclassProc(HWND hWnd, UINT uMsg, WPARAM wParam, LPARAM lParam, UINT_PTR uIdSubclass, DWORD_PTR dwRefData)
    {
        auto self = reinterpret_cast<MainWindow*>(dwRefData);
        if (self)
        {
            return self->OnWndProc(hWnd, uMsg, wParam, lParam);
        }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    LRESULT MainWindow::OnWndProc(HWND hWnd, UINT uMsg, WPARAM wParam, LPARAM lParam)
    {
        switch (uMsg)
        {
            case WM_HOTKEY:
            {
                if (wParam == 1) // Alt + Space
                {
                    ToggleVisibility(!m_isVisible);
                    return 0;
                }
                break;
            }
            case WM_ACTIVATE:
            {
                // If the window loses activation, hide it
                if (LOWORD(wParam) == WA_INACTIVE)
                {
                    ToggleVisibility(false);
                }
                break;
            }
            case WM_USER + 1: // Show message from second instance
            {
                ToggleVisibility(true);
                return 0;
            }
        }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }
}
