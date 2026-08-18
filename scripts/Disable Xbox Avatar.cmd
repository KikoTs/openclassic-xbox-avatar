@echo off
setlocal
cd /d "%~dp0"
"%~dp0OpenClassic Xbox Avatar Manager.exe" --remove
echo.
pause
exit /b %errorlevel%
