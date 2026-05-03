@echo off
setlocal
chcp 65001>nul

set "_PAUSE=1"
if /i "%~1"=="--no-pause" set "_PAUSE=0"
if /i "%CI%"=="true" set "_PAUSE=0"

echo ========================================
echo   Aemeath Build Script
echo ========================================
echo.

cd /d "%~dp0"

echo [1/4] Checking dotnet...
where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] dotnet SDK not found in PATH.
    echo Install .NET SDK 8.0+ from: https://dotnet.microsoft.com/download
    if "%_PAUSE%"=="1" pause
    exit /b 1
)

echo [2/4] Restoring packages...
dotnet restore Aemeath.sln
if errorlevel 1 (
    echo [ERROR] restore failed.
    if "%_PAUSE%"=="1" pause
    exit /b 1
)

echo [3/4] Building solution...
dotnet build Aemeath.sln -c Release --no-restore
if errorlevel 1 (
    echo [ERROR] build failed.
    if "%_PAUSE%"=="1" pause
    exit /b 1
)

echo [4/4] Publishing desktop app...
if exist "publish\Aemeath.Desktop" rmdir /s /q "publish\Aemeath.Desktop"
dotnet publish "src\Aemeath.Desktop\Aemeath.Desktop.csproj" -c Release -r win-x64 --self-contained true -o "publish\Aemeath.Desktop"
if errorlevel 1 (
    echo [ERROR] publish failed.
    if "%_PAUSE%"=="1" pause
    exit /b 1
)

echo.
echo ========================================
echo Build completed successfully.
echo Output: publish\Aemeath.Desktop\
echo ========================================
if "%_PAUSE%"=="1" pause
endlocal
