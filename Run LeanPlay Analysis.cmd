@echo off
setlocal
title LeanPlay Windows Analyzer
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Run-LeanPlayAnalysis.ps1" %*
if errorlevel 1 (
  echo.
  echo LeanPlay analysis failed. Review the message above.
  pause
  exit /b 1
)
echo.
echo LeanPlay analysis completed. The HTML report should be open in your browser.
pause
