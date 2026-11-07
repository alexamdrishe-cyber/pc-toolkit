@echo off
rem Builds the solution (build.log) and runs the tests (test.log).
rem Usage: build.cmd            (waits for a key at the end)
rem        build.cmd nopause    (for scripts)
setlocal
cd /d "%~dp0"
echo Building PC Toolkit...
dotnet build PcToolkit.slnx -c Debug -nologo -tl:off -v:minimal > build.log 2>&1
set ERR=%ERRORLEVEL%
>> build.log echo EXIT CODE: %ERR%
if %ERR% neq 0 (
  echo Build FAILED. Details are in build.log
  goto done
)
echo Build OK. Running tests...
dotnet test PcToolkit.slnx -c Debug --no-build -nologo -tl:off -v:minimal > test.log 2>&1
set ERR=%ERRORLEVEL%
>> test.log echo EXIT CODE: %ERR%
if %ERR% neq 0 (
  echo Tests FAILED. Details are in test.log
) else (
  echo Tests OK. Start the app with run.cmd
)
:done
if /i not "%~1"=="nopause" pause
endlocal & exit /b %ERR%
