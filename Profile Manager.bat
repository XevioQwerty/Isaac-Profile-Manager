@echo off
REM Launches the Isaac Profile Manager window.
REM
REM Prefers the published single-file exe. If it has not been published yet,
REM falls back to a debug build, and finally to building one.

set "HERE=%~dp0"
set "PUB=%HERE%src\IsaacProfileManager\bin\Release\net8.0-windows\win-x64\publish\IsaacProfileManager.exe"
set "DBG=%HERE%src\IsaacProfileManager\bin\Debug\net8.0-windows\win-x64\IsaacProfileManager.exe"

if exist "%PUB%" start "" "%PUB%" & exit /b
if exist "%DBG%" start "" "%DBG%" & exit /b

echo No build found. Building one now, this takes a minute...
dotnet publish "%HERE%src\IsaacProfileManager" -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
if exist "%PUB%" start "" "%PUB%" & exit /b

echo Build failed. Install the .NET 8 SDK, or run: dotnet run --project src\IsaacProfileManager
pause
