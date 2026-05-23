#pragma once
#include "MainWindow.g.h"
#include "SearchResultItem.g.h"
#include "AppScanner.h"
#include "SearchEngine.h"

namespace winrt::wincast::implementation
{
    struct SearchResultItem : SearchResultItemT<SearchResultItem>
    {
        SearchResultItem() = default;
        SearchResultItem(
            hstring const& name, 
            hstring const& path, 
            bool isUwp, 
            bool isCalculator, 
            hstring const& calcResult, 
            winrt::Microsoft::UI::Xaml::Media::Imaging::SoftwareBitmapSource const& iconSource);

        hstring Name();
        hstring Path();
        bool IsUWP();
        bool IsCalculator();
        hstring CalcResult();
        winrt::Microsoft::UI::Xaml::Media::Imaging::SoftwareBitmapSource IconSource();
        winrt::Microsoft::UI::Xaml::Visibility GetCalcVisibility();
        winrt::Microsoft::UI::Xaml::Visibility GetUwpVisibility();

    private:
        hstring m_name;
        hstring m_path;
        bool m_isUwp = false;
        bool m_isCalculator = false;
        hstring m_calcResult;
        winrt::Microsoft::UI::Xaml::Media::Imaging::SoftwareBitmapSource m_iconSource{ nullptr };
    };

    struct MainWindow : MainWindowT<MainWindow>
    {
        MainWindow();
        ~MainWindow();

        void SearchBox_TextChanged(winrt::Windows::Foundation::IInspectable const& sender, winrt::Microsoft::UI::Xaml::Controls::TextChangedEventArgs const& e);
        void SearchBox_KeyDown(winrt::Windows::Foundation::IInspectable const& sender, winrt::Microsoft::UI::Xaml::Input::KeyRoutedEventArgs const& e);
        void ResultsListView_ItemClick(winrt::Windows::Foundation::IInspectable const& sender, winrt::Microsoft::UI::Xaml::Controls::ItemClickEventArgs const& e);
        void ResultsListView_KeyDown(winrt::Windows::Foundation::IInspectable const& sender, winrt::Microsoft::UI::Xaml::Input::KeyRoutedEventArgs const& e);

    private:
        HWND m_hwnd{ nullptr };
        std::vector<::wincast::scanner::AppItem> m_apps;
        winrt::Windows::Foundation::Collections::IObservableVector<winrt::wincast::SearchResultItem> m_searchResults;
        winrt::Microsoft::UI::Dispatching::DispatcherQueue m_dispatcherQueue{ nullptr };
        winrt::Windows::Foundation::IAsyncAction m_searchAction{ nullptr };
        bool m_isScanning = false;
        bool m_isVisible = true;

        void StartAppScan();
        void UpdateSearch(std::wstring const& query);
        winrt::Windows::Foundation::IAsyncAction UpdateSearchAsync(std::wstring query);
        void LaunchSelected();
        void ToggleVisibility(bool show);

        static LRESULT CALLBACK SubclassProc(HWND hWnd, UINT uMsg, WPARAM wParam, LPARAM lParam, UINT_PTR uIdSubclass, DWORD_PTR dwRefData);
        LRESULT OnWndProc(HWND hWnd, UINT uMsg, WPARAM wParam, LPARAM lParam);
    };
}

namespace winrt::wincast::factory_implementation
{
    struct SearchResultItem : SearchResultItemT<SearchResultItem, implementation::SearchResultItem>
    {
    };
    struct MainWindow : MainWindowT<MainWindow, implementation::MainWindow>
    {
    };
}
