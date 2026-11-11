@echo off
rem Starts the rating server on this PC to check the app: http://localhost:5080 (data in server\local-data).
rem --demo fills an EMPTY local database with made-up results of popular devices, so the rating tab has something to show.
rem To point the app here, put into %LocalAppData%\PcToolkit\settings.json:  "RatingUrl": "http://localhost:5080"
rem (and remove it afterwards). Close this window to stop the server.
setlocal
cd /d "%~dp0"
set RATING_DATA=%~dp0local-data
set ASPNETCORE_URLS=http://localhost:5080
echo Rating server for checks: http://localhost:5080  (Ctrl+C or close the window to stop)
dotnet run --project PcToolkit.Rating -c Release -- --demo
pause
endlocal
