@echo off
setlocal

pushd "%~dp0" >nul || exit /b 1

where dotnet >nul 2>nul
if errorlevel 1 (
  echo The .NET 8 SDK is required and was not found on PATH.
  popd
  exit /b 1
)

set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
set "DOTNET_NOLOGO=1"
set "DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=1"
set "OFFLINE_FEED=%TEMP%\VajraShelf-empty-nuget-feed"

if not exist "%OFFLINE_FEED%" mkdir "%OFFLINE_FEED%" >nul 2>nul
if errorlevel 1 goto :failed

if exist "dist" rmdir /s /q "dist"

echo Restoring from installed SDK packs only...
dotnet restore "VajraShelf.csproj" --source "%OFFLINE_FEED%" --ignore-failed-sources -p:RestoreNoCache=true
if errorlevel 1 goto :failed

echo Building framework-dependent release...
dotnet build "VajraShelf.csproj" --configuration Release --no-restore --output "dist" -p:SelfContained=false -p:UseAppHost=true
if errorlevel 1 goto :failed

echo Build complete: %~dp0dist
popd
exit /b 0

:failed
echo Build failed.
popd
exit /b 1
