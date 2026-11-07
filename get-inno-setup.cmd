@echo off
rem Downloads Inno Setup 7.1.0 (official build, github.com/jrsoftware/issrc) and installs it
rem for the current user, without administrator rights. Log: get-inno-setup.log
rem Inno Setup is only needed to build the installer (installer.cmd), not to run PC Toolkit.
setlocal
cd /d "%~dp0"
set URL=https://github.com/jrsoftware/issrc/releases/download/is-7_1_0/innosetup-7.1.0-x64.exe
set FILE=%TEMP%\innosetup-7.1.0-x64.exe

echo Downloading Inno Setup 7.1.0...
> get-inno-setup.log echo Downloading %URL%
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue';" ^
  "[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12;" ^
  "Invoke-WebRequest -Uri '%URL%' -OutFile '%FILE%';" ^
  "$s = Get-AuthenticodeSignature '%FILE%';" ^
  "Write-Output ('Signature: ' + $s.Status + ' / ' + $s.SignerCertificate.Subject);" ^
  "if ($s.Status -ne 'Valid') { Remove-Item '%FILE%'; exit 2 }" >> get-inno-setup.log 2>&1
if errorlevel 1 goto failed
if not exist "%FILE%" goto failed

echo Installing Inno Setup for the current user...
>> get-inno-setup.log echo Installing...
"%FILE%" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /CURRENTUSER >> get-inno-setup.log 2>&1
set ERR=%ERRORLEVEL%
>> get-inno-setup.log echo Installer exit code: %ERR%
if exist "%LocalAppData%\Programs\Inno Setup 7\ISCC.exe" (
  >> get-inno-setup.log echo ISCC: %LocalAppData%\Programs\Inno Setup 7\ISCC.exe
) else if exist "%ProgramFiles%\Inno Setup 7\ISCC.exe" (
  >> get-inno-setup.log echo ISCC: %ProgramFiles%\Inno Setup 7\ISCC.exe
) else (
  goto failed
)
>> get-inno-setup.log echo EXIT CODE: 0
echo Done.
goto end

:failed
>> get-inno-setup.log echo EXIT CODE: 1
echo FAILED. Details are in get-inno-setup.log

:end
endlocal
