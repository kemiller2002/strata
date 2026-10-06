@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0praxis.ps1" %*
exit /b %ERRORLEVEL%
