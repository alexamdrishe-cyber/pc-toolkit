@echo off
rem Builds a copy of PC Toolkit for other PCs: publish\PcToolkit-<version>-win-x64.zip
rem The copy is self-contained: .NET does not need to be installed on the target PC.
rem The app is precompiled (ReadyToRun): it starts and opens screens faster.
rem Inside: PcToolkit.exe and the service folder (the Windows service the app can install).
rem Usage: publish.cmd            (waits for a key at the end)
rem        publish.cmd nopause    (for scripts)
setlocal
cd /d "%~dp0"

set VER=
for /f "usebackq delims=" %%v in (`dotnet msbuild src\PcToolkit.Desktop\PcToolkit.Desktop.csproj -getProperty:Version -nologo 2^>nul`) do set VER=%%v
if "%VER%"=="" set VER=0.1.0
set NAME=PcToolkit-%VER%-win-x64
set OUT=publish\%NAME%
set ZIP=publish\%NAME%.zip
set COMMON=-c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:GenerateDocumentationFile=false -nologo -v:minimal

rem Only this script's own previous output is replaced.
if exist "%ZIP%" del /q "%ZIP%" 2>nul
if exist "%ZIP%" (
  > publish.log echo The previous archive is open in another program: %ZIP%
  echo The previous archive is open in another program. Close it and run again.
  goto failed
)
if exist "%OUT%" rmdir /s /q "%OUT%"
if exist "%OUT%" (
  > publish.log echo The previous folder is in use, maybe PcToolkit.exe from it is running: %OUT%
  echo The previous folder is in use. Close PC Toolkit started from it and run again.
  goto failed
)

echo Publishing PC Toolkit %VER%...
> publish.log echo === PcToolkit.exe ===
dotnet publish src\PcToolkit.Desktop\PcToolkit.Desktop.csproj %COMMON% -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishReadyToRun=true -p:ValidateExecutableReferencesMatchSelfContained=false -o "%OUT%" >> publish.log 2>&1
if errorlevel 1 goto failed
rem The service settings file lands here via the project reference; the service has its own copy.
if exist "%OUT%\PcToolkit.Service.runtimeconfig.json" del /q "%OUT%\PcToolkit.Service.runtimeconfig.json"

>> publish.log echo === service\PcToolkit.Service.exe ===
rem Own build folder: the self-contained build must not mix into bin\Release\...\service next to the app.
dotnet publish src\PcToolkit.Service\PcToolkit.Service.csproj %COMMON% "-p:OutDir=%~dp0src\PcToolkit.Service\obj\publish-build" -o "%OUT%\service" >> publish.log 2>&1
if errorlevel 1 goto failed

>> publish.log echo === zip ===
tar -a -c -f "%ZIP%" -C publish "%NAME%" >> publish.log 2>&1
if errorlevel 1 (
  if exist "%ZIP%" del /q "%ZIP%" 2>nul
  powershell -NoProfile -Command "Compress-Archive -Path '%OUT%' -DestinationPath '%ZIP%'" >> publish.log 2>&1
  if errorlevel 1 goto failed
)
if not exist "%ZIP%" goto failed

>> publish.log echo EXIT CODE: 0
echo Done: %ZIP%
set ERR=0
goto done

:failed
>> publish.log echo EXIT CODE: 1
echo Publish FAILED. Details are in publish.log
set ERR=1

:done
if /i not "%~1"=="nopause" pause
endlocal & exit /b %ERR%
