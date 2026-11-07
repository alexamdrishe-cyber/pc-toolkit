@echo off
rem Same as build.cmd, but closes the window when done (for automated checks).
call "%~dp0build.cmd" nopause
