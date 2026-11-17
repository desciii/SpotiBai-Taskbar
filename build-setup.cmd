@echo off
setlocal
PowerShell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-setup.ps1" %*
exit /b %ERRORLEVEL%
