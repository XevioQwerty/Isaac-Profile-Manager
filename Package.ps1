<#
.SYNOPSIS
    Build the release artifacts: a portable zip and a Windows installer.

.DESCRIPTION
    Publishes the self-contained app, stages it alongside the 32-bit Steam
    helper, and produces:

        dist\IsaacProfileManager-v<version>-win-x64.zip
        dist\IsaacProfileManager-Setup-v<version>.exe

    The zip is always built. The installer needs Inno Setup 6; if ISCC.exe is
    not found the zip is still produced and the script says how to get it.

    The helper is a separate executable on purpose: Isaac ships only a 32-bit
    steam_api.dll, so it cannot live inside the 64-bit app. Both artifacts must
    carry it or the Workshop update and share-import features are dead on
    arrival.

.EXAMPLE
    .\Package.ps1
    .\Package.ps1 -SkipInstaller
#>

[CmdletBinding()]
param(
    [switch]$SkipInstaller,
    [switch]$SkipZip
)

$ErrorActionPreference = 'Stop'

$root      = $PSScriptRoot
$project   = Join-Path $root 'src\IsaacProfileManager'
$dist      = Join-Path $root 'dist'
$portable  = Join-Path $dist 'portable'
$appExe    = 'IsaacProfileManager.exe'
$helperExe = 'ipm-steam-helper.exe'

Write-Host ''
Write-Host '  Isaac Profile Manager - package' -ForegroundColor Cyan
Write-Host ''

# --- Version ------------------------------------------------------------
# Single source of truth is the csproj; the installer and the zip name both
# follow it so a release cannot ship three different version numbers.
$csproj  = Join-Path $project 'IsaacProfileManager.csproj'
$version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> in $csproj" }
Write-Host "  Version $version" -ForegroundColor Gray

# --- Publish ------------------------------------------------------------
Write-Host '  Publishing (this takes a minute)...' -ForegroundColor Gray
dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Publish failed. Is the .NET 8 SDK installed?' }

$publishDir = Join-Path $project 'bin\Release\net8.0-windows\win-x64\publish'
foreach ($file in $appExe, $helperExe) {
    $path = Join-Path $publishDir $file
    if (-not (Test-Path $path)) { throw "Publish did not produce $file at $path" }
}

# --- Stage --------------------------------------------------------------
if (Test-Path $portable) { Remove-Item $portable -Recurse -Force }
New-Item -ItemType Directory -Path $portable -Force | Out-Null

Copy-Item (Join-Path $publishDir $appExe)    $portable -Force
Copy-Item (Join-Path $publishDir $helperExe) $portable -Force
Copy-Item (Join-Path $root 'README.md')      $portable -Force
Copy-Item (Join-Path $root 'LICENSE')        $portable -Force

$staged = Get-ChildItem $portable | Measure-Object -Property Length -Sum
Write-Host ("  Staged {0} files, {1:N0} MB" -f $staged.Count, ($staged.Sum / 1MB)) -ForegroundColor Gray

# --- Zip ----------------------------------------------------------------
if (-not $SkipZip) {
    $zip = Join-Path $dist "IsaacProfileManager-v$version-win-x64.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }

    # 7-Zip when it is around: the payload is a 160 MB single-file bundle and
    # Compress-Archive is markedly slower and weaker on it.
    $sevenZip = @(
        'C:\Program Files\7-Zip\7z.exe',
        'C:\Program Files (x86)\7-Zip\7z.exe'
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $sevenZip) { $sevenZip = (Get-Command 7z.exe -ErrorAction SilentlyContinue).Source }

    if ($sevenZip) {
        Write-Host '  Zipping with 7-Zip...' -ForegroundColor Gray
        & $sevenZip a -tzip -mx=9 $zip (Join-Path $portable '*') | Out-Null
        if ($LASTEXITCODE -ne 0) { throw '7-Zip failed.' }
    } else {
        Write-Host '  Zipping with Compress-Archive...' -ForegroundColor Gray
        Compress-Archive -Path (Join-Path $portable '*') -DestinationPath $zip -CompressionLevel Optimal
    }

    Write-Host ("  Zip:       {0}  ({1:N0} MB)" -f $zip, ((Get-Item $zip).Length / 1MB)) -ForegroundColor Green
}

# --- Installer ----------------------------------------------------------
if (-not $SkipInstaller) {
    # winget installs Inno per-user under LOCALAPPDATA, not Program Files.
    $iscc = @(
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe',
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source }

    if ($iscc) {
        Write-Host '  Building the installer...' -ForegroundColor Gray
        $script = Join-Path $root 'installer\IsaacProfileManager.iss'
        & $iscc "/DAppVersion=$version" "/DSourceDir=$portable" $script | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed.' }

        $setup = Join-Path $dist "IsaacProfileManager-Setup-v$version.exe"
        Write-Host ("  Installer: {0}  ({1:N0} MB)" -f $setup, ((Get-Item $setup).Length / 1MB)) -ForegroundColor Green
    } else {
        Write-Host '  Inno Setup 6 not found, so no installer was built.' -ForegroundColor Yellow
        Write-Host '  Install it and re-run:' -ForegroundColor Yellow
        Write-Host '      winget install --id JRSoftware.InnoSetup -e' -ForegroundColor Yellow
    }
}

Write-Host ''
Write-Host "  Done. Artifacts are in $dist" -ForegroundColor Green
Write-Host ''
