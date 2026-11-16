@echo off
setlocal
title TSP100 Bridge - build

set "DOTNET=C:\Program Files\dotnet\dotnet.exe"
if not exist "%DOTNET%" (
  echo dotnet.exe was not found at "%DOTNET%".
  echo Install the .NET 8 SDK for Windows Arm64 from https://dotnet.microsoft.com/download/dotnet/8.0
  pause
  exit /b 1
)

pushd "%~dp0src"
if errorlevel 1 (echo Cannot open the src folder next to build.cmd & pause & exit /b 1)

taskkill /im TSP100Bridge.exe /f >nul 2>&1
timeout /t 1 /nobreak >nul
echo Building TSP100Bridge for Windows on ARM64 ...
"%DOTNET%" publish TSP100Bridge.csproj -c Release -r win-arm64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "%~dp0bin"
if errorlevel 1 (echo. & echo BUILD FAILED - copy the red error lines above and send them to Claude. & popd & pause & exit /b 1)
popd

echo.
echo Done: %~dp0bin\TSP100Bridge.exe
start "" "%~dp0bin\TSP100Bridge.exe"
pause
