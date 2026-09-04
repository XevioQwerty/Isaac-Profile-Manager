<#
.SYNOPSIS
    Read-only snapshot of everything Isaac writes per save slot, for probes.

.DESCRIPTION
    Prints size, last-write time and SHA-1 for every file the game or REPENTOGON
    is known to write for a save: Steam's userdata folder, the Documents folder,
    each mod's data\ folder, REPENTOGON's per-slot JSON, plus log.txt's version
    line and Steam's Cloud toggle. Run it before and after a manual step, then
    diff the two outputs to see what moved.

    Reads only. Never writes into any of the folders it lists.

.EXAMPLE
    .\tools\Snapshot-SaveState.ps1 > before.txt
    # launch the game, do the thing, close it
    .\tools\Snapshot-SaveState.ps1 > after.txt
    git diff --no-index before.txt after.txt
#>
[CmdletBinding()]
param(
    [string]$GameDir,
    [string]$AppId = '250900'
)

$ErrorActionPreference = 'Stop'

function Get-ConfigValue([string]$key) {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'IsaacProfileManager\isaac-profiles.json'),
        (Join-Path $PSScriptRoot '..\isaac-profiles.json')
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) {
            $json = Get-Content $c -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($json.$key) { return $json.$key }
        }
    }
    return $null
}

if (-not $GameDir) { $GameDir = Get-ConfigValue 'GameDir' }

$steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
$documents = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'My Games\Binding of Isaac Repentance+'

function Describe([string]$path, [string]$label) {
    if (-not (Test-Path $path)) { return }
    $f = Get-Item $path
    # The game holds log.txt open while it runs; a locked file is reported, not fatal.
    try { $sha = (Get-FileHash $path -Algorithm SHA1 -ErrorAction Stop).Hash.ToLowerInvariant() }
    catch { $sha = '(locked)     ' }
    [pscustomobject]@{
        Where = $label
        File  = $f.Name
        Bytes = $f.Length
        Written = $f.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss')
        Sha1  = $sha.Substring(0, 12)
    }
}

$rows = @()

# 1. Steam userdata, every account that has the app
if ($steam -and (Test-Path "$steam\userdata")) {
    foreach ($acct in Get-ChildItem "$steam\userdata" -Directory) {
        $remote = Join-Path $acct.FullName "$AppId\remote"
        if (-not (Test-Path $remote)) { continue }
        foreach ($f in Get-ChildItem $remote -File) {
            $rows += Describe $f.FullName "userdata\$($acct.Name)\remote"
        }
        $cache = Join-Path $acct.FullName "$AppId\remotecache.vdf"
        $rows += Describe $cache "userdata\$($acct.Name)"

        $shared = Join-Path $acct.FullName "7\remote\sharedconfig.vdf"
        if (Test-Path $shared) {
            $text = Get-Content $shared -Raw
            $m = [regex]::Match($text, "`"$AppId`"\s*\{[^}]*`"cloudenabled`"\s*`"(\d)`"")
            $rows += [pscustomobject]@{
                Where = "userdata\$($acct.Name)\7"
                File = 'sharedconfig.vdf cloudenabled'
                Bytes = 0
                Written = (Get-Item $shared).LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss')
                Sha1 = if ($m.Success) { $m.Groups[1].Value } else { '(absent = on)' }
            }
        }
    }
}

# 2. Documents: the non-Steam location, log.txt and REPENTOGON's state
if (Test-Path $documents) {
    foreach ($f in Get-ChildItem $documents -File | Where-Object { $_.Extension -in '.dat', '.ini', '.txt' }) {
        $rows += Describe $f.FullName 'Documents'
    }
    $rgon = Join-Path $documents 'Repentogon'
    if (Test-Path $rgon) {
        foreach ($f in Get-ChildItem $rgon -File -Filter '*.json') {
            $rows += Describe $f.FullName 'Documents\Repentogon'
        }
    }
    $log = Join-Path $documents 'log.txt'
    if (Test-Path $log) {
        $version = (Select-String -Path $log -Pattern 'Game Version:\s*(\S+)' | Select-Object -First 1).Matches[0].Groups[1].Value
        $cmd = (Get-Content $log -TotalCount 120 | Select-String -Pattern '^\s*\[INFO\] - \s*-' | Select-Object -First 3) -join ' '
        $rows += [pscustomobject]@{ Where = 'log.txt'; File = "Game Version: $version"; Bytes = (Get-Item $log).Length; Written = (Get-Item $log).LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'); Sha1 = $cmd }
    }
}

# 3. Mod save data
if ($GameDir -and (Test-Path (Join-Path $GameDir 'data'))) {
    foreach ($mod in Get-ChildItem (Join-Path $GameDir 'data') -Directory) {
        foreach ($f in Get-ChildItem $mod.FullName -File -Filter 'save*.dat') {
            $rows += Describe $f.FullName "data\$($mod.Name)"
        }
    }
}

# 4. The game's own idea of where it saves
if ($GameDir) {
    $sdp = Join-Path $GameDir 'savedatapath.txt'
    if (Test-Path $sdp) {
        $rows += [pscustomobject]@{ Where = 'GameDir'; File = 'savedatapath.txt'; Bytes = (Get-Item $sdp).Length; Written = (Get-Item $sdp).LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'); Sha1 = ((Get-Content $sdp | Select-String 'Save Data Path') -join '') }
    }
}

"Snapshot at $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')   Steam running: $([bool](Get-Process steam -ErrorAction SilentlyContinue))   Isaac running: $([bool](Get-Process isaac-ng -ErrorAction SilentlyContinue))"
$rows | Sort-Object Where, File | Format-Table -AutoSize | Out-String -Width 200
