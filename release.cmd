@echo off
rem Publishes a new version of PC Toolkit on GitHub: fresh build, installer, SHA-256 and a GitHub release.
rem Installed copies find the release by themselves (banner on Home, tray notification, Settings - Updates).
rem
rem Needs once:
rem   1. GitHub CLI:  winget install GitHub.cli   then   gh auth login
rem   2. Directory.Build.props: <GitHubRepo>owner/repo</GitHubRepo> and a new <Version> for every release
rem   3. release-notes.md: what's new in this version (users see it in the app)
rem Usage: release.cmd        (asks before publishing)
rem        release.cmd yes    (no question)
setlocal
cd /d "%~dp0"
set ERR=1

where gh >nul 2>nul
if errorlevel 1 (
  echo GitHub CLI not found. Install it: winget install GitHub.cli  -  then run: gh auth login
  goto done
)
gh auth status >nul 2>nul
if errorlevel 1 (
  echo GitHub CLI is not signed in. Run once: gh auth login
  goto done
)

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

set DIRTY=
for /f "usebackq delims=" %%s in (`git status --porcelain`) do set DIRTY=1
if defined DIRTY (
  echo There are uncommitted changes. Commit them first: the release must match the code on GitHub.
  goto done
)

gh release view v%VER% -R %REPO% >nul 2>nul
if not errorlevel 1 (
  echo Release v%VER% already exists on github.com/%REPO%. Raise Version in Directory.Build.props.
  goto done
)

echo === PC Toolkit %VER% for github.com/%REPO% ===
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

for /f "usebackq delims=" %%b in (`git rev-parse --abbrev-ref HEAD`) do set BRANCH=%%b
git remote get-url origin >nul 2>nul
if errorlevel 1 git remote add origin https://github.com/%REPO%.git
gh auth setup-git >nul 2>nul
git push -u origin %BRANCH%
if errorlevel 1 (
  echo git push failed.
  goto done
)

gh release create v%VER% "%SETUP%" "%SETUP%.sha256" -R %REPO% --target %BRANCH% --title "PC Toolkit %VER%" --notes-file release-notes.md
if errorlevel 1 (
  echo Release was not created.
  goto done
)
echo Done: https://github.com/%REPO%/releases/tag/v%VER%
set ERR=0

:done
if /i not "%~1"=="yes" pause
endlocal & exit /b %ERR%
