@echo off
rem Diagnostic: start the newest published app reading sensors itself, as on a PC without the service.
set APP=
for /d %%d in ("%~dp0PcToolkit-*-win-x64") do set APP=%%d
if "%APP%"=="" (
  echo No published app next to this file. Run publish.cmd first.
  pause
  exit /b 1
)
start "" "%APP%\PcToolkit.exe" --no-service
