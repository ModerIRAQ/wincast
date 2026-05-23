# WinCast

WinCast is a fast Windows launcher built with WinUI 3. It opens with `Alt + Space` and gives you app search, calculator results, shell command launching, recent apps, tray support, preview details, and modern rounded material themes.

## Features

- Global `Alt + Space` launcher hotkey
- Fuzzy app search across Start Menu shortcuts and UWP apps
- Calculator expressions directly in the search box
- Shell command mode with `>` prefix
- Recent app launcher dashboard
- Optional preview/details pane
- System tray menu
- Mica, Acrylic, and Solid visual modes
- Built-in update checker for GitHub Releases

## Download

Download the latest installer from:

[github.com/ModerIRAQ/wincast/releases/latest](https://github.com/ModerIRAQ/wincast/releases/latest)

Use `WinCastSetup-x64-vX.Y.Z.exe` for normal installation.

## Build From Source

Requirements:

- Windows 10 1809 or newer
- .NET 8 SDK
- Visual Studio 2022/2026 Windows development tools, or equivalent Windows App SDK build prerequisites
- Inno Setup 6, only required for installer builds

Build:

```powershell
dotnet build .\WinCast.csproj -c Debug -p:Platform=x64
```

Publish:

```powershell
dotnet publish .\WinCast.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:PublishDir=artifacts\publish\win-x64\
```

Build installer after publishing:

```powershell
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" installer\WinCast.iss /DAppVersion=0.1.0
```

## Release Process

Releases are built by GitHub Actions when a semantic version tag is pushed:

```powershell
git tag v0.1.0
git push origin v0.1.0
```

The workflow publishes a GitHub Release with an Inno Setup installer asset.

## Updates

WinCast checks:

```text
https://api.github.com/repos/ModerIRAQ/wincast/releases/latest
```

When a newer release exists, WinCast downloads the setup installer, starts it, and exits so the installer can update locked files safely.

## License

MIT. See [LICENSE](LICENSE).
