@echo off
rem Same as installer.cmd, but closes the window when done (for automated checks).
call "%~dp0installer.cmd" nopause
