@echo off
rem Closes a running PC Toolkit (installed or built) and starts the freshly built one.
rem Used for checking a build without reinstalling: build-quiet.cmd, then run-fresh.cmd.
setlocal
cd /d "%~dp0"
set EXE=src\PcToolkit.Desktop\bin\Debug\net10.0-windows\PcToolkit.exe
if not exist "%EXE%" (
  echo The app is not built yet. Run build.cmd first.
  pause
  exit /b 1
)
"%EXE%" --exit
rem give the running copy time to release the single-instance lock
ping -n 4 127.0.0.1 >nul
start "" "%EXE%"
endlocal