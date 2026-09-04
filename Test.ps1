<#
.SYNOPSIS
    Build everything and run the checks that catch real breakage.

.DESCRIPTION
    Three levels, because they cost very different amounts:

      (default)   build both executables and run the unit tests.
      -Live       also talk to Steam's public API and the local Steam client,
                  read-only. Proves the network and interop paths still work
                  without changing anything.
      -Install    also publish and install over the local install.

    The live checks are the ones that catch what unit tests cannot: a Steam
    endpoint changing shape, or the 32-bit helper failing to bind against the
    game's steam_api.dll after a game update.

.EXAMPLE
    .\Test.ps1
    .\Test.ps1 -Live
    .\Test.ps1 -Live -Install
#>

[CmdletBinding()]
param(
    [switch]$Live,
    [switch]$Install
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$failures = [System.Collections.Generic.List[string]]::new()

function Step($name) {
    Write-Host ''
    Write-Host "  == $name" -ForegroundColor Cyan
}

<#
    Run a native exe and return its stdout lines.

    Start-Process with redirection to files rather than the call operator: in
    PowerShell 5.1 a native command's stderr becomes a NativeCommandError, which
    $ErrorActionPreference = 'Stop' turns terminating. steam_api.dll prints a
    breakpad banner to stderr on every run, so calling it inline reports a
    perfectly healthy helper as broken -- and whether it does depends on how
    this script was itself invoked, which is worse.
#>
function Invoke-Exe($path, [string[]]$exeArgs, $timeoutSeconds = 300) {
    $out = [System.IO.Path]::GetTempFileName()
    $err = [System.IO.Path]::GetTempFileName()

    try {
        # Start-Process joins ArgumentList with spaces and quotes nothing, so a
        # path containing spaces arrives as several arguments. The game folder
        # is "The Binding of Isaac Rebirth", which the helper then read as a
        # published file id.
        $quoted = $exeArgs | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }

        $p = Start-Process -FilePath $path -ArgumentList ($quoted -join ' ') -NoNewWindow -PassThru `
                           -RedirectStandardOutput $out -RedirectStandardError $err
        if (-not $p.WaitForExit($timeoutSeconds * 1000)) {
            try { $p.Kill() } catch {}
            return @()
        }
        $p.WaitForExit()   # the timed overload can return before output is flushed
        return @(Get-Content $out -ErrorAction SilentlyContinue)
    } finally {
        Remove-Item $out, $err -Force -ErrorAction SilentlyContinue
    }
}

function Check($name, [scriptblock]$test) {
    try {
        $result = & $test
        if ($result -eq $false) { $failures.Add($name); Write-Host "  FAIL  $name" -ForegroundColor Red }
        else { Write-Host "  ok    $name" -ForegroundColor Green }
    } catch {
        $failures.Add("$name -- $($_.Exception.Message)")
        Write-Host "  FAIL  $name -- $($_.Exception.Message)" -ForegroundColor Red
    }
}

Write-Host ''
Write-Host '  Isaac Profile Manager - test' -ForegroundColor Cyan

# --- Build -------------------------------------------------------------
Step 'Build'
dotnet build "$root\IsaacProfileManager.sln" --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Host '  ok    solution builds' -ForegroundColor Green

# --- Unit tests --------------------------------------------------------
Step 'Unit tests'
dotnet test "$root\tests\IsaacProfileManager.Tests\IsaacProfileManager.Tests.csproj" --nologo -v quiet
if ($LASTEXITCODE -ne 0) { $failures.Add('unit tests') }

# --- Versions match ----------------------------------------------------
Step 'Version stamping'
$version = ([xml](Get-Content "$root\Directory.Build.props")).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1

$app    = "$root\src\IsaacProfileManager\bin\Debug\net8.0-windows\win-x64\IsaacProfileManager.exe"
$helper = "$root\src\IsaacProfileManager.SteamHelper\bin\Debug\net8.0\win-x86\ipm-steam-helper.exe"

Check "app and helper both stamped $version" {
    foreach ($exe in $app, $helper) {
        if (-not (Test-Path $exe)) { return $false }
        if ((Get-Item $exe).VersionInfo.FileVersion -notlike "$version*") { return $false }
    }
    $true
}

if ($Live) {
    # --- Steam's public API --------------------------------------------
    # No key, no client, no subscription. If these break, update checking and
    # share-code imports stop working and nothing else would notice.
    Step 'Steam public API (read-only)'

    Check 'GetPublishedFileDetails returns time_updated' {
        $body = @{ itemcount = 1; 'publishedfileids[0]' = '836319872' }
        $r = Invoke-RestMethod -Method Post -Uri 'https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/' -Body $body
        $d = $r.response.publishedfiledetails[0]
        ($d.result -eq 1) -and ($d.time_updated -gt 0) -and ($null -ne $d.preview_url)
    }

    Check 'GetCollectionDetails resolves children' {
        $body = @{ collectioncount = 1; 'publishedfileids[0]' = '3775687336' }
        $r = Invoke-RestMethod -Method Post -Uri 'https://api.steampowered.com/ISteamRemoteStorage/GetCollectionDetails/v1/' -Body $body
        $r.response.collectiondetails[0].children.Count -gt 0
    }

    # --- The 32-bit helper against the real client ---------------------
    Step 'Steam helper (read-only)'

    $config = Get-Content "$env:LOCALAPPDATA\IsaacProfileManager\isaac-profiles.json" -ErrorAction SilentlyContinue | ConvertFrom-Json
    $gameDir = $config.GameDir

    if (-not $gameDir) {
        Write-Host '  skip  no config found, so the helper cannot be checked' -ForegroundColor Yellow
    } else {
        Check 'helper connects to Steam and reports ownership' {
            $out = Invoke-Exe $helper @('status', '--game-dir', $gameDir) 60 |
                   Where-Object { $_ -like '{*' }
            if (-not $out) { return $false }
            $ready = $out | Select-Object -First 1 | ConvertFrom-Json
            # ownsApp false is a real answer about the account, not a failure here.
            $null -ne $ready.subscribed
        }

        Check 'helper gives up on an unsubscribable id instead of hanging' {
            $started = Get-Date
            Invoke-Exe $helper @('pull', '--game-dir', $gameDir, '--timeout', '600',
                                 '--stall', '15', '999999999') 120 | Out-Null
            ((Get-Date) - $started).TotalSeconds -lt 90
        }
    }

    # --- The save-sync Worker, if one is configured ---------------------
    # Uses a throwaway key and set name, and deletes its own lane after.
    Step 'Save sync endpoint (throwaway lane)'
    $endpoint = $config.SaveSyncEndpoint

    if (-not $endpoint) {
        Write-Host '  skip  no SaveSyncEndpoint in the config' -ForegroundColor Yellow
    } else {
        Check 'Worker answers ping' {
            (Invoke-RestMethod -Uri "$endpoint/v1/ping").ok -eq $true
        }

        Check 'push, list, pull, delete round trip through the real client' {
            $env:IPM_SYNC_ENDPOINT = $endpoint
            try {
                dotnet test "$root\tests\IsaacProfileManager.Tests\IsaacProfileManager.Tests.csproj" --nologo -v quiet --no-build `
                    --filter 'FullyQualifiedName~SaveSyncLiveTests' | Out-Null
                $LASTEXITCODE -eq 0
            } finally {
                Remove-Item Env:IPM_SYNC_ENDPOINT -ErrorAction SilentlyContinue
            }
        }
    }
}

if ($Install) {
    Step 'Install'
    & "$root\Install.ps1" -Destination (Join-Path $env:LOCALAPPDATA 'IsaacProfileManager') -NoShortcut
}

# --- Verdict -----------------------------------------------------------
Write-Host ''
if ($failures.Count -eq 0) {
    Write-Host '  All checks passed.' -ForegroundColor Green
    Write-Host ''
    exit 0
}

Write-Host "  $($failures.Count) check(s) failed:" -ForegroundColor Red
$failures | ForEach-Object { Write-Host "    - $_" -ForegroundColor Red }
Write-Host ''
exit 1
