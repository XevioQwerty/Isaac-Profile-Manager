<#
.SYNOPSIS
    Build Isaac Profile Manager and install it somewhere permanent.

.DESCRIPTION
    Publishes the self-contained exe and copies it to a stable folder, so you
    launch a real program instead of a .bat, and can pin it to the taskbar.

    The default target is %LOCALAPPDATA%\Programs\IsaacProfileManager, which
    needs no administrator. Pass -Destination to put it anywhere else.

    Safe to re-run: it is how you update after pulling changes.

.EXAMPLE
    .\Install.ps1
    .\Install.ps1 -Destination 'D:\Apps\IsaacProfileManager'
    .\Install.ps1 -NoShortcut
#>

[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\IsaacProfileManager'),
    [switch]$NoShortcut,
    [switch]$DesktopShortcut
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\IsaacProfileManager'
$exeName = 'IsaacProfileManager.exe'

Write-Host ''
Write-Host '  Isaac Profile Manager - install' -ForegroundColor Cyan
Write-Host ''

# --- Build --------------------------------------------------------------
Write-Host '  Publishing (this takes a minute the first time)...' -ForegroundColor Gray
dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Publish failed. Is the .NET 8 SDK installed?' }

$published = Join-Path $project "bin\Release\net8.0-windows\win-x64\publish\$exeName"
if (-not (Test-Path $published)) { throw "Published exe not found at $published" }

# --- Install ------------------------------------------------------------
if (-not (Test-Path $Destination)) { New-Item -ItemType Directory -Path $Destination -Force | Out-Null }
$target = Join-Path $Destination $exeName

# The app may be running - it cannot overwrite itself while it is.
$running = Get-Process -Name 'IsaacProfileManager' -ErrorAction SilentlyContinue
if ($running) {
    Write-Host '  Isaac Profile Manager is open. Close it and press Enter to continue.' -ForegroundColor Yellow
    Read-Host
}

Copy-Item $published $target -Force
$version = (Get-Item $target).VersionInfo.FileVersion
Write-Host "  Installed v$version to $target" -ForegroundColor Green

# --- Shortcuts ----------------------------------------------------------
if (-not $NoShortcut) {
    $shell = New-Object -ComObject WScript.Shell

    $startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
    $lnk = $shell.CreateShortcut((Join-Path $startMenu 'Isaac Profile Manager.lnk'))
    $lnk.TargetPath       = $target
    $lnk.WorkingDirectory = $Destination
    $lnk.Description      = 'Switch Isaac mod profiles, saves and builds'
    $lnk.IconLocation     = "$target,0"
    $lnk.Save()
    Write-Host '  Added to the Start Menu - search for "Isaac Profile Manager"' -ForegroundColor Green

    if ($DesktopShortcut) {
        $desktop = [Environment]::GetFolderPath('Desktop')
        $lnk2 = $shell.CreateShortcut((Join-Path $desktop 'Isaac Profile Manager.lnk'))
        $lnk2.TargetPath       = $target
        $lnk2.WorkingDirectory = $Destination
        $lnk2.IconLocation     = "$target,0"
        $lnk2.Save()
        Write-Host '  Added a Desktop shortcut' -ForegroundColor Green
    }
}

Write-Host ''
Write-Host '  Done. Launch it from the Start Menu, or pin it to your taskbar.' -ForegroundColor White
Write-Host "  Re-run this script after pulling changes to update." -ForegroundColor Gray
Write-Host ''
