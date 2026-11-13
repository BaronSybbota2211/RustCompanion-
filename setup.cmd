@echo off
setlocal
chcp 65001 >nul
cd /d "%~dp0"
set LOG=%~dp0setup.log
echo === Rust Companion setup === > "%LOG%"

where dotnet >nul 2>&1 || (echo [ERROR] .NET SDK not found. Install .NET 10 SDK: https://dotnet.microsoft.com/download & goto :fail)
for /f "tokens=1 delims=." %%v in ('dotnet --version') do set MAJOR=%%v
echo .NET SDK:
dotnet --version
if %MAJOR% LSS 10 (echo [ERROR] .NET SDK 10 or newer is required, found %MAJOR%. & goto :fail)

tasklist /fi "imagename eq RustCompanion.exe" 2>nul | find /i "RustCompanion.exe" >nul && (echo [ERROR] Rust Companion is running. Close it and run setup again. & goto :fail)

echo [1/6] Restoring packages...
dotnet restore src\RustCompanion.App\RustCompanion.App.csproj >> "%LOG%" 2>&1 || (echo [ERROR] NuGet restore failed, see setup.log & goto :fail)

echo [2/6] Compiling...
dotnet build src\RustCompanion.App\RustCompanion.App.csproj -c Release --no-restore >> "%LOG%" 2>&1 || (echo [ERROR] Compile failed. Error lines: & findstr /i /c:": error " "%LOG%" & goto :fail)

echo [3/6] Tests (optional, failures do not stop the setup)...
dotnet test tests\RustCompanion.Tests -c Release >> "%LOG%" 2>&1 && echo Tests passed. || echo [WARNING] Tests failed or did not build, see setup.log

echo [4/6] Publishing exe...
dotnet publish src\RustCompanion.App\RustCompanion.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish >> "%LOG%" 2>&1 || (echo [ERROR] Publish failed. Error lines: & findstr /i /c:": error " "%LOG%" & goto :fail)

echo [5/6] Installing to %LOCALAPPDATA%\Programs\RustCompanion ...
set "INSTALL=%LOCALAPPDATA%\Programs\RustCompanion"
if not exist "%INSTALL%" mkdir "%INSTALL%"
xcopy /y /q /e "publish\*" "%INSTALL%\" >> "%LOG%" 2>&1 || (echo [ERROR] Copy to "%INSTALL%" failed, see setup.log & goto :fail)
copy /y "src\RustCompanion.App\Resources\app.ico" "%INSTALL%\app.ico" >nul
set "EXE=%INSTALL%\RustCompanion.exe"
if not exist "%EXE%" (echo [ERROR] %EXE% not found after copy. & goto :fail)

echo [6/6] Creating desktop shortcut (program icon)...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$d=[Environment]::GetFolderPath('Desktop'); $s=(New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $d 'Rust Companion.lnk')); $s.TargetPath=$env:EXE; $s.WorkingDirectory=$env:INSTALL; $s.IconLocation=($env:EXE + ',0'); $s.Description='Rust Companion'; $s.Save()" >> "%LOG%" 2>&1 || (echo [ERROR] Could not create the desktop shortcut, see setup.log & goto :fail)
rem Launcher for this script with the same icon (a .cmd file cannot carry an icon itself)
powershell -NoProfile -ExecutionPolicy Bypass -Command "$s=(New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path '%~dp0' 'Rust Companion Setup.lnk')); $s.TargetPath='%~dp0setup.cmd'; $s.WorkingDirectory='%~dp0'; $s.IconLocation=($env:INSTALL + '\app.ico'); $s.Description='Rust Companion setup'; $s.Save()" >> "%LOG%" 2>&1

echo.
echo Done. Installed: %EXE%
echo Desktop shortcut: Rust Companion
pause
exit /b 0

:fail
echo.
echo Setup stopped. Full log: %LOG%
echo Send me the error lines above (or the setup.log file).
pause
exit /b 1
