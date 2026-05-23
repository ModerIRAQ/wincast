#include "pch.h"
#include "App.xaml.h"
#include "MainWindow.xaml.h"
#include "Generated Files\SearchResultItem.g.cpp"

// Provide our own App default constructor - the generated one has issues with composable XAML types
WINRT_EXPORT namespace winrt::wincast
{
    App::App()
    {
        *this = winrt::make<implementation::App>().as<App>();
    }
}
