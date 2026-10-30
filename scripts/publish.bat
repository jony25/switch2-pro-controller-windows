@echo off
setlocal
REM Builds dist\Switch2ProManager.exe (single file, self-contained: no .NET runtime needed to run it).
REM Only the .NET 10 SDK is required to build:  winget install Microsoft.DotNet.SDK.10
where dotnet >nul 2>nul
if errorlevel 1 (
  echo "dotnet" not found. Install the .NET 10 SDK:  winget install Microsoft.DotNet.SDK.10
  exit /b 1
)
dotnet publish "%~dp0..\src\Switch2ProManager\Switch2ProManager.csproj" -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true ^
  -o "%~dp0..\dist"
if errorlevel 1 (
  echo.
  echo Build FAILED.
  exit /b 1
)
echo.
echo Done: dist\Switch2ProManager.exe
