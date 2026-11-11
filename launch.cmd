@echo off
setlocal
pushd "%~dp0" >nul || exit /b 1

if not exist "dist\VajraShelf.exe" (
  echo Vajra Shelf has not been built. Run build.cmd first.
  popd
  exit /b 1
)

start "Vajra Shelf" "dist\VajraShelf.exe" %*
set "RESULT=%ERRORLEVEL%"
popd
exit /b %RESULT%
