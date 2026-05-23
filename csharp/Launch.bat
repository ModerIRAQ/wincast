@echo off
cd /d "%~dp0"
set DOTNET_ROOT=C:\Users\moder\dotnet
set PATH=%DOTNET_ROOT%;%PATH%
dotnet run
