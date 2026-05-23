#pragma once
#include "App.g.h"

namespace winrt::wincast::implementation
{
    struct App : AppT<App>
    {
        App();
        void OnLaunched(winrt::Microsoft::UI::Xaml::LaunchActivatedEventArgs const&);
    private:
        winrt::Microsoft::UI::Xaml::Window m_window{ nullptr };
    };
}

namespace winrt::wincast::factory_implementation
{
    struct App : AppT<App, implementation::App>
    {
    };
}
