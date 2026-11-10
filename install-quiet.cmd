@echo off
rem Installs the newest built installer without the wizard (Windows still asks for permission once).
rem Order for a check on this PC: build-quiet.cmd, installer-quiet.cmd, install-quiet.cmd.
setlocal
cd /d "%~dp0"
set SETUP=
for /f "delims=" %%f in ('dir /b /o:d "publish\PcToolkit-*-Setup.exe" 2^>nul') do set SETUP=%%f
if not defined SETUP (
  > install.log echo No installer in publish. Run installer-quiet.cmd first.
  exit /b 1
)
> install.log echo Installing %SETUP%...
"publish\%SETUP%" /SILENT /SUPPRESSMSGBOXES /NORESTART /LOG="%~dp0install-setup.log"
set ERR=%ERRORLEVEL%
>> install.log echo EXIT CODE: %ERR%
if %ERR%==0 start "" "%ProgramFiles%\PC Toolkit\PcToolkit.exe"
endlocal & exit /b %ERR%
