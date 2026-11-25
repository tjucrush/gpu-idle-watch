@echo off
setlocal
where node.exe >nul 2>&1
if errorlevel 1 (
  echo Please install Node.js 20 or later and add it to PATH.
  pause
  exit /b 1
)
node.exe "%~dp0launch.mjs" %*
if errorlevel 1 pause
