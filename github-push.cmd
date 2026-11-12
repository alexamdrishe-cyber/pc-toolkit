@echo off
rem Sends the project to GitHub (GitHubRepo in Directory.Build.props).
rem The git history comes from history\pc-toolkit.bundle (the copy that is kept in sync with the code), so this folder
rem becomes a normal git working copy. The first push asks to sign in to GitHub in the browser (Git Credential Manager,
rem part of Git for Windows) - only once, then Windows remembers it.
rem Usage: github-push.cmd [nopause]
setlocal
cd /d "%~dp0"
set ERR=1

where git >nul 2>nul
if errorlevel 1 (
  echo Git is not installed: https://git-scm.com/download/win
  goto done
)
set REPO=
for /f "usebackq delims=" %%v in (`dotnet msbuild src\PcToolkit.Desktop\PcToolkit.Desktop.csproj -getProperty:GitHubRepo -nologo 2^>nul`) do set REPO=%%v
if "%REPO%"=="" (
  echo Set the repository in Directory.Build.props first: ^<GitHubRepo^>owner/repo^</GitHubRepo^>
  goto done
)

if not exist .git (
  git init -q -b main
  if errorlevel 1 goto done
)
if exist history\pc-toolkit.bundle (
  git fetch -q --no-tags history\pc-toolkit.bundle +refs/heads/main:refs/remotes/bundle/main
  if errorlevel 1 (
    echo Could not read history\pc-toolkit.bundle.
    goto done
  )
  git rev-parse -q --verify refs/heads/main >nul 2>nul
  if not errorlevel 1 (
    git merge-base --is-ancestor refs/heads/main refs/remotes/bundle/main
    if errorlevel 1 (
      echo This folder has commits that are not in history\pc-toolkit.bundle - leaving them as they are.
      goto done
    )
  )
  rem The files here are already up to date: move the branch and the index, do not touch the files.
  git update-ref refs/heads/main refs/remotes/bundle/main
  git symbolic-ref HEAD refs/heads/main
  git reset -q --mixed
)

set DIRTY=
for /f "usebackq delims=" %%s in (`git status --porcelain --untracked-files=no`) do set DIRTY=1
if defined DIRTY (
  echo These files differ from the last commit and are NOT sent:
  git status --short --untracked-files=no
  echo.
)

git remote get-url origin >nul 2>nul
if errorlevel 1 git remote add origin https://github.com/%REPO%.git
git push -u origin main
if errorlevel 1 (
  echo Push failed.
  goto done
)
echo Done: https://github.com/%REPO%
set ERR=0

:done
if /i not "%~1"=="nopause" pause
endlocal & exit /b %ERR%
