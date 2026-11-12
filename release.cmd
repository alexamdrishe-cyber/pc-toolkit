@echo off
rem Publishes a new version of PC Toolkit on GitHub: sends the code (github-push.cmd), fresh build, installer,
rem SHA-256 and a GitHub release. Installed copies find the release by themselves (banner on Home, tray
rem notification, Settings - Updates).
rem
rem Needs: Git for Windows (signs in to GitHub in the browser on the first push); GitHub CLI is not required.
rem Before every release: a new <Version> in Directory.Build.props and what is new in release-notes.md
rem (users see it in the app).
rem Usage: release.cmd        (asks before publishing)
rem        release.cmd yes    (no question)
setlocal
cd /d "%~dp0"
set ERR=1

set REPO=
for /f "usebackq delims=" %%v in (`dotnet msbuild src\PcToolkit.Desktop\PcToolkit.Desktop.csproj -getProperty:GitHubRepo -nologo 2^>nul`) do set REPO=%%v
if "%REPO%"=="" (
  echo Set the repository in Directory.Build.props first: ^<GitHubRepo^>owner/repo^</GitHubRepo^>
  goto done
)
set VER=
for /f "usebackq delims=" %%v in (`dotnet msbuild src\PcToolkit.Desktop\PcToolkit.Desktop.csproj -getProperty:Version -nologo 2^>nul`) do set VER=%%v
if "%VER%"=="" (
  echo Could not read Version from Directory.Build.props.
  goto done
)
if not exist release-notes.md (
  echo Write what is new in %VER% to release-notes.md first.
  goto done
)

echo === Sending the code to github.com/%REPO% ===
call github-push.cmd nopause
if errorlevel 1 goto done

set DIRTY=
for /f "usebackq delims=" %%s in (`git status --porcelain --untracked-files=no`) do set DIRTY=1
if defined DIRTY (
  echo Some files differ from the last commit: the release must match the code on GitHub.
  goto done
)

powershell -NoProfile -ExecutionPolicy Bypass -File tools\github-release.ps1 -Repo %REPO% -Version %VER% -Branch main -NotesFile release-notes.md -Files x -CheckOnly
if errorlevel 1 goto done

echo === PC Toolkit %VER% ===
call publish.cmd nopause
if errorlevel 1 goto done
call installer.cmd nopause
if errorlevel 1 goto done

set SETUP=publish\PcToolkit-%VER%-Setup.exe
if not exist "%SETUP%" (
  echo Installer not found: %SETUP%
  goto done
)
powershell -NoProfile -Command "$h=(Get-FileHash -Algorithm SHA256 -LiteralPath '%SETUP%').Hash.ToLower(); Set-Content -LiteralPath '%SETUP%.sha256' -Value ($h + '  PcToolkit-%VER%-Setup.exe') -Encoding ascii"
if errorlevel 1 (
  echo Could not compute SHA-256.
  goto done
)

if /i not "%~1"=="yes" (
  choice /m "Publish PC Toolkit %VER% on github.com/%REPO% now"
  if errorlevel 2 (
    echo Cancelled. The installer is in the publish folder.
    set ERR=0
    goto done
  )
)

powershell -NoProfile -ExecutionPolicy Bypass -File tools\github-release.ps1 -Repo %REPO% -Version %VER% -Branch main -NotesFile release-notes.md -Files "%SETUP%;%SETUP%.sha256"
if errorlevel 1 (
  echo Release was not created.
  goto done
)
set ERR=0

:done
if /i not "%~1"=="yes" pause
endlocal & exit /b %ERR%
