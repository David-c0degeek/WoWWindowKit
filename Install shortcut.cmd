@echo off
rem Add Start menu and desktop shortcuts. Supports -NoDesktop and -Remove.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-shortcut.ps1" %*
pause
