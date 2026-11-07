@echo off
rem Same as publish.cmd, but closes the window when done (for automated checks).
call "%~dp0publish.cmd" nopause
