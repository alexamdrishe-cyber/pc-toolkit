@echo off
rem Builds the PC Toolkit installer: publish\PcToolkit-<version>-Setup.exe (log: installer.log).
rem Needs Inno Setup 7: get-inno-setup.cmd installs it for the current user.
rem If the app for this version is not published yet, runs publish.cmd first.
rem Usage: installer.cmd            (waits for a key at the end)
rem        installer.cmd nopause    (for scripts)
setlocal
cd /d "%~dp0"

set ISCC=
for %%p in (
  "%LocalAppData%\Programs\Inno Setup 7\ISCC.exe"
  "%ProgramFiles%\Inno Setup 7\ISCC.exe"
  "%ProgramFiles(x86)%\Inno Setup 7\ISCC.exe"
) do if not defined ISCC if exist %%p set ISCC=%%~p
if not defined ISCC (
  > installer.log echo Inno Setup not found. Run get-inno-setup.cmd first.
  echo Inno Setup not found. Run get-inno-setup.cmd first.
  set ERR=1
  goto done
)

set VER=
for /f "usebackq delims=" %%v in (`dotnet msbuild src\PcToolkit.Desktop\PcToolkit.Desktop.csproj -getProperty:Version -nologo 2^>nul`) do set VER=%%v
if "%VER%"=="" set VER=0.1.0
set APPFILES=%~dp0publish\PcToolkit-%VER%-win-x64

if not exist "%APPFILES%\PcToolkit.exe" (
  echo The app %VER% is not published yet. Running publish.cmd...
  call publish.cmd nopause
  if errorlevel 1 (
    > installer.log echo publish.cmd failed, see publish.log
    set ERR=1
    goto done
  )
)

echo Building the installer for PC Toolkit %VER%...
"%ISCC%" /Q "/DAppVersion=%VER%" "/DAppFiles=%APPFILES%" "/O%~dp0publish" installer\PcToolkit.iss > installer.log 2>&1
set ERR=%ERRORLEVEL%
>> installer.log echo EXIT CODE: %ERR%
if %ERR% neq 0 (
  echo Installer build FAILED. Details are in installer.log
) else (
  echo Done: publish\PcToolkit-%VER%-Setup.exe
)

:done
if /i not "%~1"=="nopause" pause
endlocal & exit /b %ERR%
