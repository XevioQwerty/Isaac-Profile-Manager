@echo off
title Isaac Profile Manager - Setup
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0IsaacProfiles.ps1" -Setup
echo.
pause
