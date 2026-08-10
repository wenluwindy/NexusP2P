@echo off
rem Chinese text lives in the .ps1 to avoid batch codepage trouble.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-signaling.ps1" %*
if errorlevel 1 pause
