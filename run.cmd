@echo off
rem Starts the built app. Builds it first if needed.
setlocal
cd /d "%~dp0"
set EXE=src\PcToolkit.Desktop\bin\Debug\net10.0-windows\PcToolkit.exe
if not exist "%EXE%" (
  echo The app is not built yet. Running build.cmd...
  call build.cmd nopause
  if errorlevel 1 (
    echo Build failed, see build.log
    pause
    exit /b 1
  )
)
start "" "%EXE%"
endlocal
