@echo off
setlocal
pushd "%~dp0" >nul || exit /b 1

call "%~dp0build.cmd"
if errorlevel 1 goto :failed

set "RELEASE_DIR=%~dp0release"
set "STAGE_DIR=%TEMP%\VajraShelf-release-%RANDOM%-%RANDOM%"
set "ZIP_PATH=%RELEASE_DIR%\VajraShelf-framework-dependent.zip"

if exist "%STAGE_DIR%" rmdir /s /q "%STAGE_DIR%"
mkdir "%STAGE_DIR%\VajraShelf" >nul 2>nul
if errorlevel 1 goto :failed
if not exist "%RELEASE_DIR%" mkdir "%RELEASE_DIR%" >nul 2>nul
if errorlevel 1 goto :cleanup_failed

for %%F in (VajraShelf.exe VajraShelf.dll VajraShelf.deps.json VajraShelf.runtimeconfig.json Microsoft.Windows.SDK.NET.dll WinRT.Runtime.dll) do (
    if not exist "dist\%%F" (
        echo Missing required package file: %%F
        goto :cleanup_failed
    )
    copy /y "dist\%%F" "%STAGE_DIR%\VajraShelf\%%F" >nul
)
copy /y "README.md" "%STAGE_DIR%\VajraShelf\README.md" >nul
if errorlevel 1 goto :cleanup_failed
copy /y "LICENSE" "%STAGE_DIR%\VajraShelf\LICENSE" >nul
if errorlevel 1 goto :cleanup_failed

if exist "%ZIP_PATH%" del /q "%ZIP_PATH%"
powershell.exe -NoLogo -NoProfile -NonInteractive -Command "Compress-Archive -LiteralPath (Join-Path $env:STAGE_DIR 'VajraShelf') -DestinationPath $env:ZIP_PATH -CompressionLevel Optimal"
if errorlevel 1 goto :cleanup_failed

rmdir /s /q "%STAGE_DIR%"
echo Release package: %ZIP_PATH%
popd
exit /b 0

:cleanup_failed
if exist "%STAGE_DIR%" rmdir /s /q "%STAGE_DIR%"
:failed
echo Release packaging failed.
popd
exit /b 1
