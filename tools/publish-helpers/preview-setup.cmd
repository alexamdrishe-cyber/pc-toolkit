@echo off
rem Preview of the installer wizard without administrator rights (/CURRENTUSER: no service, user folder).
set SETUP=
for %%f in ("%~dp0PcToolkit-*-Setup.exe") do set SETUP=%%f
start "" "%SETUP%" /CURRENTUSER
