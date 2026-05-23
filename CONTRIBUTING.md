# Contributing

Thanks for helping improve WinCast.

## Development

Use Windows with the .NET 8 SDK and WinUI/Windows App SDK build tools installed.

```powershell
dotnet build .\WinCast.csproj -c Debug -p:Platform=x64
```

Before opening a pull request:

- Keep changes focused.
- Run a clean build.
- Avoid committing generated `bin`, `obj`, `artifacts`, or log files.
- Include screenshots or short recordings for UI changes when possible.

## Releases

Maintainers publish releases by pushing a tag such as `v0.1.0`. GitHub Actions builds the self-contained x64 publish output, compiles the Inno Setup installer, and uploads it to the release.
