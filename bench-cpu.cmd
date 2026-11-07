@echo off
rem CPU score calibration: builds tools\CpuBench (Release) and runs the CPU benchmark several times.
rem Prints medians per workload; on the reference CPU (Ryzen 5 5600) they become CpuScore.Reference.
rem Usage: bench-cpu.cmd [runs]   (default 3, about 40 seconds each). Output: bench-cpu.log
setlocal
cd /d "%~dp0"
set RUNS=%~1
if "%RUNS%"=="" set RUNS=3
echo CPU benchmark, %RUNS% runs. Close games and heavy programs. Output: bench-cpu.log
dotnet run -c Release --project tools\CpuBench\CpuBench.csproj -- %RUNS% > bench-cpu.log 2>&1
set ERR=%ERRORLEVEL%
>> bench-cpu.log echo EXIT CODE: %ERR%
endlocal & exit /b %ERR%
