#pragma once
#define NOMINMAX
#include <windows.h>
#undef GetCurrentTime
#include <unknwn.h>
#include <restrictederrorinfo.h>
#include <hstring.h>

// Windows App SDK / WinUI 3 namespaces
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.ApplicationModel.Activation.h>
#include <winrt/Windows.ApplicationModel.Core.h>
#include <winrt/Windows.System.h>
#include <winrt/Windows.Management.Deployment.h>
#include <winrt/Windows.ApplicationModel.h>
#include <winrt/Windows.Storage.h>
#include <winrt/Windows.Storage.Streams.h>
#include <winrt/Microsoft.UI.h>
#include <winrt/Microsoft.UI.Content.h>
#include <winrt/Microsoft.UI.Xaml.h>
#include <winrt/Microsoft.UI.Xaml.Controls.h>
#include <winrt/Microsoft.UI.Xaml.Controls.Primitives.h>
#include <winrt/Microsoft.UI.Xaml.Data.h>
#include <winrt/Microsoft.UI.Xaml.Interop.h>
#include <winrt/Microsoft.UI.Xaml.Markup.h>
#include <winrt/Microsoft.UI.Xaml.Navigation.h>
#include <winrt/Microsoft.UI.Xaml.Input.h>
#include <winrt/Microsoft.UI.Xaml.Media.h>
#include <winrt/Microsoft.UI.Xaml.Media.Imaging.h>
#include <winrt/Microsoft.UI.Dispatching.h>
#include <winrt/Microsoft.UI.Windowing.h>

// Windows Implementation Library (WIL)
#include <wil/resource.h>
#include <wil/result.h>
#include <wil/com.h>

// C++ Standard Library
#include <vector>
#include <string>
#include <string_view>
#include <memory>
#include <algorithm>
#include <thread>
#include <mutex>
#include <shared_mutex>
#include <future>
#include <sstream>
#include <unordered_map>
#include <filesystem>
#include <chrono>

// Win32/COM helpers
#include <shlobj.h>
#include <shlwapi.h>
#include <shellapi.h>
#include <propkey.h>
#include <propvarutil.h>
#include <dwmapi.h>

#include <coroutine>

// App namespaces shorthand
#include "winrt/wincast.h"

namespace winrt
{
    using namespace Windows::Foundation;
    using namespace Windows::Foundation::Collections;
    using namespace Microsoft::UI;
    using namespace Microsoft::UI::Xaml;
    using namespace Microsoft::UI::Xaml::Controls;
    using namespace Microsoft::UI::Xaml::Media;
    using namespace Microsoft::UI::Xaml::Input;
    using namespace Microsoft::UI::Dispatching;
}

inline auto resume_dispatcher(winrt::Microsoft::UI::Dispatching::DispatcherQueue const& dispatcher)
{
    struct awaitable
    {
        winrt::Microsoft::UI::Dispatching::DispatcherQueue const& dispatcher;
        bool await_ready() const noexcept { return dispatcher.HasThreadAccess(); }
        void await_suspend(std::coroutine_handle<> handle) const
        {
            dispatcher.TryEnqueue([handle]() { handle.resume(); });
        }
        void await_resume() const noexcept {}
    };
    return awaitable{ dispatcher };
}
