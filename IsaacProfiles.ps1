<#
.SYNOPSIS
    Switch between named Binding of Isaac mod profiles.

.DESCRIPTION
    Each profile is a folder of mods. Switching re-points the game's mods\
    directory at that folder using a directory junction, so it is instant and
    nothing is copied.

    This tool does NOT launch the game. Switch a profile, then start Isaac
    however you normally do (Steam, the REPENTOGON launcher, a shortcut).

.PARAMETER Setup
    First-time setup wizard.

.PARAMETER Use
    Name of the profile to activate.

.PARAMETER List
    Show all profiles and which one is active.

.PARAMETER Add
    Create a new empty profile with this name.

.PARAMETER Remove
    Remove a profile from the config (the folder is left on disk).

.EXAMPLE
    .\IsaacProfiles.ps1 -Setup
    .\IsaacProfiles.ps1 -Use vanilla-coop
    .\IsaacProfiles.ps1 -Add challenge-run
    .\IsaacProfiles.ps1 -List
#>

[CmdletBinding()]
param(
    [switch]$Setup,
    [string]$Use,
    [switch]$List,
    [string]$Add,
    [string]$Remove
)

$ErrorActionPreference = 'Stop'

# EDIT ME before sharing: shown to people who skip Syncthing.
$Script:RepoUrl    = 'https://github.com/XevioQwerty/Isaac-Profile-Manager'
$Script:ConfigPath = Join-Path $PSScriptRoot 'isaac-profiles.json'
$Script:SteamAppId = 250900

# ---------------------------------------------------------------------------
# Output
# ---------------------------------------------------------------------------

function Write-Head { param($t) Write-Host ''; Write-Host "  $t" -ForegroundColor Cyan; Write-Host ("  " + ('-' * $t.Length)) -ForegroundColor DarkGray }
function Write-Ok   { param($t) Write-Host "  [ok]   $t" -ForegroundColor Green }
function Write-Info { param($t) Write-Host "  [info] $t" -ForegroundColor Gray }
function Write-Warn { param($t) Write-Host "  [warn] $t" -ForegroundColor Yellow }
function Write-Err  { param($t) Write-Host "  [fail] $t" -ForegroundColor Red }

function Read-Default {
    param([string]$Prompt, [string]$Default)
    $answer = Read-Host "  $Prompt [$Default]"
    if ([string]::IsNullOrWhiteSpace($answer)) { return $Default }
    return $answer.Trim().Trim('"')
}

function Read-YesNo {
    param([string]$Prompt, [bool]$DefaultYes = $true)
    $hint = if ($DefaultYes) { 'Y/n' } else { 'y/N' }
    while ($true) {
        $answer = Read-Host "  $Prompt [$hint]"
        if ([string]::IsNullOrWhiteSpace($answer)) { return $DefaultYes }
        switch -Regex ($answer.Trim()) {
            '^(y|yes)$' { return $true }
            '^(n|no)$'  { return $false }
            default     { Write-Warn 'Please answer y or n.' }
        }
    }
}

# ---------------------------------------------------------------------------
# Pickers
# ---------------------------------------------------------------------------

function Test-GuiAvailable {
    if ($null -ne $Script:GuiOk) { return $Script:GuiOk }
    try {
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
        # WinForms dialogs need a single-threaded apartment. powershell.exe is
        # STA by default; pwsh 7 is not, so check rather than assume.
        $Script:GuiOk = ([Threading.Thread]::CurrentThread.GetApartmentState() -eq 'STA')
    } catch { $Script:GuiOk = $false }
    return $Script:GuiOk
}

function Select-FolderPath {
    param([string]$Description, [string]$Default)
    if (Test-GuiAvailable) {
        Write-Info "$Description  (a folder picker has opened)"
        $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
        $dlg.Description = $Description
        if ($Default -and (Test-Path -LiteralPath $Default)) { $dlg.SelectedPath = $Default }
        if ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { return $dlg.SelectedPath }
        Write-Info 'Picker cancelled - type a path instead.'
    }
    return (Read-Default $Description $Default)
}

function Select-FilePath {
    param([string]$Title, [string]$Filter, [string]$StartDir)
    if (Test-GuiAvailable) {
        Write-Info "$Title  (a file picker has opened)"
        $dlg = New-Object System.Windows.Forms.OpenFileDialog
        $dlg.Title  = $Title
        $dlg.Filter = $Filter
        if ($StartDir -and (Test-Path -LiteralPath $StartDir)) { $dlg.InitialDirectory = $StartDir }
        if ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { return $dlg.FileName }
        Write-Info 'Picker cancelled - type a path instead.'
    }
    $typed = Read-Host "  $Title"
    return $typed.Trim().Trim('"')
}

# ---------------------------------------------------------------------------
# Junctions
#
# A junction looks like a real folder to most tools, and a recursive delete
# aimed at one can follow the link and wipe the TARGET. Everything here deletes
# links only, via the .NET call that cannot recurse.
# ---------------------------------------------------------------------------

function Test-IsJunction {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    $item = Get-Item -LiteralPath $Path -Force
    return [bool]($item.Attributes -band [IO.FileAttributes]::ReparsePoint)
}

function Get-JunctionTarget {
    param([string]$Path)
    if (-not (Test-IsJunction $Path)) { return $null }
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.PSObject.Properties.Name -contains 'Target' -and $item.Target) {
        return @($item.Target)[0]
    }
    return $null
}

function Remove-JunctionLink {
    param([string]$Path)
    if (-not (Test-IsJunction $Path)) {
        throw "Refusing to delete '$Path' - it is not a junction. Move it aside manually."
    }
    [System.IO.Directory]::Delete($Path, $false)   # false = never recurse
}

function New-ProfileJunction {
    param([string]$LinkPath, [string]$TargetPath)
    if (-not (Test-Path -LiteralPath $TargetPath)) {
        throw "Profile folder does not exist: $TargetPath"
    }
    New-Item -ItemType Junction -Path $LinkPath -Target $TargetPath -Force | Out-Null
}

# ---------------------------------------------------------------------------
# Discovery
# ---------------------------------------------------------------------------

function Get-DocumentsPath {
    # Not $env:USERPROFILE\Documents - OneDrive redirection breaks that.
    return [Environment]::GetFolderPath('MyDocuments')
}

function Get-LauncherIniPath {
    return Join-Path (Get-DocumentsPath) 'My Games\repentogon_launcher.ini'
}

function Find-IsaacExe {
    $ini = Get-LauncherIniPath
    if (Test-Path -LiteralPath $ini) {
        $match = Select-String -LiteralPath $ini -Pattern '^\s*IsaacExecutable\s*=\s*(.+?)\s*$' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($match) {
            $candidate = $match.Matches[0].Groups[1].Value.Trim()
            if (Test-Path -LiteralPath $candidate) {
                Write-Info 'Found game path in repentogon_launcher.ini'
                return $candidate
            }
        }
    }
    foreach ($drive in (Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue)) {
        foreach ($sub in @('Program Files (x86)\Steam','Steam','SteamLibrary')) {
            $g = Join-Path $drive.Root "$sub\steamapps\common\The Binding of Isaac Rebirth\isaac-ng.exe"
            if (Test-Path -LiteralPath $g) { Write-Info 'Found game at a standard Steam path'; return $g }
        }
    }
    return $null
}

function Find-RepentogonLauncher {
    # The launcher must NOT live inside the game install - the official docs
    # warn against extracting it there, and specifically against a folder named
    # "repentogon" in the game dir (that name belongs to the downgraded build).
    param([string]$GameDir)
    $parent = Split-Path $GameDir -Parent
    $roots = @($parent) + @(Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue | ForEach-Object { $_.Root })
    foreach ($root in ($roots | Select-Object -Unique)) {
        foreach ($name in @('REPENTOGONLauncher','Repentogon','RepentogonLauncher')) {
            $c = Join-Path $root "$name\REPENTOGONLauncher.exe"
            if (Test-Path -LiteralPath $c -PathType Leaf) { return $c }
        }
    }
    try {
        $hit = Get-ChildItem -LiteralPath $parent -Recurse -Depth 2 -Filter 'REPENTOGONLauncher.exe' `
                             -File -Force -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($hit) { return $hit.FullName }
    } catch { }
    return $null
}

function Resolve-LauncherPath {
    # Accept the exe itself or a folder containing it.
    # NOTE: not named $Input - that is a PowerShell automatic variable.
    param([string]$PathText)
    if ([string]::IsNullOrWhiteSpace($PathText)) { return $null }
    $p = $PathText.Trim().Trim('"')
    if (Test-Path -LiteralPath $p -PathType Leaf) {
        if ([IO.Path]::GetFileName($p) -ieq 'REPENTOGONLauncher.exe') { return $p }
        return $null
    }
    if (Test-Path -LiteralPath $p -PathType Container) {
        $c = Join-Path $p 'REPENTOGONLauncher.exe'
        if (Test-Path -LiteralPath $c -PathType Leaf) { return $c }
    }
    return $null
}

function Get-SyncthingInfo {
    foreach ($base in @($env:LOCALAPPDATA, $env:APPDATA)) {
        $cfg = Join-Path $base 'Syncthing\config.xml'
        if (Test-Path -LiteralPath $cfg) {
            try {
                [xml]$xml = Get-Content -LiteralPath $cfg -Raw
                return [pscustomobject]@{
                    ConfigPath = $cfg
                    ApiKey     = $xml.configuration.gui.apikey
                    Address    = $xml.configuration.gui.address
                }
            } catch { }
        }
    }
    return $null
}

# ---------------------------------------------------------------------------
# Config
# ---------------------------------------------------------------------------

function Get-Config {
    if (-not (Test-Path -LiteralPath $Script:ConfigPath)) { return $null }
    try { return Get-Content -LiteralPath $Script:ConfigPath -Raw | ConvertFrom-Json }
    catch { Write-Warn "Config file is unreadable: $Script:ConfigPath"; return $null }
}

function Save-Config {
    param($Config)
    $Config | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Script:ConfigPath -Encoding UTF8
}

function Test-ProfileName {
    param([string]$Name)
    if ([string]::IsNullOrWhiteSpace($Name)) { return $false }
    # Becomes a folder name and part of a .bat filename.
    return ($Name -notmatch '[\\/:*?"<>|]')
}

# ---------------------------------------------------------------------------
# REPENTOGON build selection
#
# The launcher reads [Shared] LaunchMode from its ini to decide which build to
# start. 1 = REPENTOGON, 0 = vanilla (the game then gets --repentogonoff).
# Writing it here means the build follows the profile even though we never
# launch anything ourselves.
# ---------------------------------------------------------------------------

function Set-LauncherMode {
    param([int]$Mode)
    $ini = Get-LauncherIniPath
    if (-not (Test-Path -LiteralPath $ini)) {
        Write-Info 'No launcher ini found - skipping build selection.'
        return $false
    }
    $lines = Get-Content -LiteralPath $ini
    $out = @(); $inShared = $false; $written = $false
    foreach ($line in $lines) {
        if ($line -match '^\s*\[(.+?)\]\s*$') {
            if ($inShared -and -not $written) { $out += "LaunchMode = $Mode"; $written = $true }
            $inShared = ($Matches[1] -eq 'Shared')
        }
        if ($inShared -and $line -match '^\s*LaunchMode\s*=') {
            $out += "LaunchMode = $Mode"; $written = $true
        } else {
            $out += $line
        }
    }
    if ($inShared -and -not $written) { $out += "LaunchMode = $Mode"; $written = $true }
    if (-not $written) { $out += '[Shared]'; $out += "LaunchMode = $Mode" }
    Set-Content -LiteralPath $ini -Value $out -Encoding UTF8
    return $true
}

function Remove-DisableMarkers {
    param([string]$ProfileDir)
    $markers = @(Get-ChildItem -LiteralPath $ProfileDir -Recurse -Filter 'disable.it' -Force -ErrorAction SilentlyContinue)
    if ($markers.Count -gt 0) {
        $markers | Remove-Item -Force -ErrorAction SilentlyContinue
        Write-Info "Cleared $($markers.Count) disable.it marker(s)"
    }
    return $markers.Count
}

# ---------------------------------------------------------------------------
# Generated switch scripts and shortcuts
# ---------------------------------------------------------------------------

function Write-SwitchScript {
    param([string]$Name)
    $file = Join-Path $PSScriptRoot "Switch to $Name.bat"
    $body = @"
@echo off
REM Generated by IsaacProfiles.ps1 - safe to delete and regenerate.
REM Switches the active mod profile. Does NOT launch the game.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0IsaacProfiles.ps1" -Use "$Name"
timeout /t 4 >nul
"@
    Set-Content -LiteralPath $file -Value $body -Encoding ASCII
    return $file
}

function New-Shortcut {
    param([string]$TargetPath, [string]$ShortcutPath, [string]$IconPath, [string]$WorkDir)
    try {
        $shell = New-Object -ComObject WScript.Shell
        $lnk = $shell.CreateShortcut($ShortcutPath)
        $lnk.TargetPath       = $TargetPath
        $lnk.WorkingDirectory = $WorkDir
        $lnk.Description      = 'Switch Isaac mod profile'
        if ($IconPath -and (Test-Path -LiteralPath $IconPath)) { $lnk.IconLocation = "$IconPath,0" }
        $lnk.Save()
        return $true
    } catch {
        Write-Warn "Could not create shortcut: $($_.Exception.Message)"
        return $false
    }
}

function Publish-Profile {
    # Regenerate the .bat and any shortcuts for one profile.
    param($Config, [string]$Name)
    $bat = Write-SwitchScript $Name
    Write-Ok "Wrote '$(Split-Path $bat -Leaf)'"
    foreach ($dir in @($Config.ShortcutDirs)) {
        if (-not $dir) { continue }
        $lnk = Join-Path $dir "Isaac profile - $Name.lnk"
        if (New-Shortcut -TargetPath $bat -ShortcutPath $lnk -IconPath $Config.IsaacExe -WorkDir $PSScriptRoot) {
            Write-Ok "Shortcut: $lnk"
        }
    }
}

# ---------------------------------------------------------------------------
# Setup
# ---------------------------------------------------------------------------

function Invoke-Setup {
    Write-Host ''
    Write-Host '  =========================================' -ForegroundColor Cyan
    Write-Host '   Isaac Profile Manager - first-time setup' -ForegroundColor Cyan
    Write-Host '  =========================================' -ForegroundColor Cyan
    Write-Info 'This switches mod profiles. It does not launch the game.'

    # --- Game --------------------------------------------------------------
    Write-Head 'Step 1 of 6: Locate the game'
    $exe = Find-IsaacExe
    if ($exe) {
        Write-Host "  Detected: $exe" -ForegroundColor White
        if (-not (Read-YesNo 'Use this installation?')) { $exe = $null }
    }
    if (-not $exe) {
        $exe = Select-FilePath 'Select isaac-ng.exe' 'Isaac executable (isaac-ng.exe)|isaac-ng.exe|All executables (*.exe)|*.exe' $null
    }
    if (-not $exe -or -not (Test-Path -LiteralPath $exe)) { Write-Err 'No valid executable selected. Aborting.'; return }

    $gameDir = Split-Path -Parent $exe
    $modsDir = Join-Path $gameDir 'mods'
    Write-Ok "Game directory: $gameDir"

    # --- Sync root ---------------------------------------------------------
    Write-Head 'Step 2 of 6: Choose where profiles live'
    Write-Info 'One folder holds every profile. This is what you sync or version.'
    Write-Info 'Keep it OUTSIDE the game directory.'
    $defaultRoot = Join-Path ([IO.Path]::GetPathRoot($gameDir)) 'IsaacProfiles'
    $syncRoot = Select-FolderPath 'Choose the folder to hold your mod profiles' $defaultRoot
    if ([string]::IsNullOrWhiteSpace($syncRoot)) { Write-Err 'No folder chosen. Aborting.'; return }
    if ($syncRoot.TrimEnd('\').ToLower().StartsWith($gameDir.TrimEnd('\').ToLower())) {
        Write-Err 'That path is inside the game directory. Pick somewhere else.'
        return
    }
    if (-not (Test-Path -LiteralPath $syncRoot)) { New-Item -ItemType Directory -Path $syncRoot -Force | Out-Null }
    Write-Ok "Profiles folder: $syncRoot"

    # --- Profiles ----------------------------------------------------------
    Write-Head 'Step 3 of 6: Name your profiles'
    Write-Info 'Name them after what they are for, e.g. "coop-with-alex",'
    Write-Info '"heavy-modded", "vanilla-plus", "challenge-run". Add more later'
    Write-Info 'with:  IsaacProfiles.ps1 -Add <name>'
    Write-Host ''
    $existing = @(Get-ChildItem -LiteralPath $syncRoot -Directory -ErrorAction SilentlyContinue |
                  Where-Object { $_.Name -notmatch '^\.' } | Select-Object -ExpandProperty Name)
    if ($existing.Count -gt 0) {
        Write-Info "Folders already here: $($existing -join ', ')"
        if (Read-YesNo 'Use those as your profiles?') { $profileNames = $existing }
    }
    if (-not $profileNames) {
        $raw = Read-Default 'Profile names (comma separated)' 'modded,vanilla'
        $profileNames = @($raw -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    }
    $profileNames = @($profileNames | Where-Object { Test-ProfileName $_ } | Select-Object -Unique)
    if ($profileNames.Count -eq 0) { Write-Err 'No usable profile names. Aborting.'; return }

    foreach ($p in $profileNames) {
        $dir = Join-Path $syncRoot $p
        if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    }
    Write-Ok "Profiles: $($profileNames -join ', ')"

    # Sync/VCS metadata sits above the profile folders so Isaac never
    # enumerates it - it treats every subfolder of mods\ as a candidate mod.
    $stignore = Join-Path $syncRoot '.stignore'
    if (-not (Test-Path -LiteralPath $stignore)) {
        Set-Content -LiteralPath $stignore -Encoding UTF8 -Value @(
            '// Never let Syncthing replicate a live git directory.'
            '/.git'; '/.gitignore'; '/.gitattributes'; '/.stversions'
            '(?d)desktop.ini'; '(?d)Thumbs.db'
        )
        Write-Ok 'Wrote .stignore'
    }
    $gitignore = Join-Path $syncRoot '.gitignore'
    if (-not (Test-Path -LiteralPath $gitignore)) {
        Set-Content -LiteralPath $gitignore -Encoding UTF8 -Value @(
            '# Syncthing metadata - machine-local, never commit'
            '.stfolder/'; '.stversions/'; '.stignore'; '.syncthing.*.tmp'
        )
        Write-Ok 'Wrote .gitignore'
    }
    $gitattr = Join-Path $syncRoot '.gitattributes'
    if (-not (Test-Path -LiteralPath $gitattr)) {
        Set-Content -LiteralPath $gitattr -Encoding UTF8 -Value @(
            '# Byte-for-byte identical checkouts. Without this git rewrites line'
            '# endings in .lua/.xml and a clone differs from a Syncthing copy.'
            '* -text'
        )
        Write-Ok 'Wrote .gitattributes'
    }

    # --- Migrate -----------------------------------------------------------
    Write-Head 'Step 4 of 6: Migrate your current mods'
    if (Test-IsJunction $modsDir) {
        Write-Info 'mods\ is already a junction - nothing to migrate.'
    } elseif (Test-Path -LiteralPath $modsDir) {
        $folders = @(Get-ChildItem -LiteralPath $modsDir -Directory -ErrorAction SilentlyContinue)
        Write-Info "Found $($folders.Count) mod folder(s) in the game's mods directory."
        if ($folders.Count -gt 0) {
            Write-Host ''
            for ($i = 0; $i -lt $profileNames.Count; $i++) { Write-Host "    [$($i+1)] copy into '$($profileNames[$i])'" }
            Write-Host '    [a] copy into ALL profiles (prune each later)'
            Write-Host '    [s] skip'
            Write-Host ''
            $choice = Read-Default 'Choice' 'a'
            $targets = @()
            if ($choice -eq 'a') { $targets = $profileNames }
            elseif ($choice -eq 's') { $targets = @() }
            elseif ($choice -match '^\d+$' -and [int]$choice -ge 1 -and [int]$choice -le $profileNames.Count) {
                $targets = @($profileNames[[int]$choice - 1])
            } else { $targets = $profileNames }

            foreach ($p in $targets) {
                $dest = Join-Path $syncRoot $p
                Write-Info "Copying into '$p'..."
                foreach ($f in $folders) { Copy-Item -LiteralPath $f.FullName -Destination $dest -Recurse -Force }
                Write-Ok "Populated '$p'"
            }
        }
    } else {
        Write-Info 'No mods directory yet - it will be created as a junction.'
    }

    # Migrated folders almost always carry disable.it from whenever the mod was
    # last switched off, so a mod is present but silently disabled. Always clear.
    Write-Host ''
    $cleared = 0
    foreach ($p in $profileNames) { $cleared += Remove-DisableMarkers (Join-Path $syncRoot $p) }
    if ($cleared -eq 0) { Write-Info 'No stale disable.it markers found.' }
    else { Write-Ok "Cleared $cleared stale disable.it marker(s) - those mods are now enabled." }

    # --- How you launch ----------------------------------------------------
    Write-Head 'Step 5 of 6: How do you launch Isaac?'
    Write-Info 'This tool only switches profiles - you start the game yourself.'
    Write-Host ''
    Write-Host '    [1] Steam'
    Write-Host '    [2] REPENTOGON launcher directly'
    Write-Host '    [3] Something else / not sure'
    Write-Host ''
    $launchChoice = Read-Default 'Choice' '1'
    $ownsOnSteam = ($launchChoice -eq '1')

    $launcherExe = $null
    $perProfileBuild = $false
    if ($launchChoice -in @('1','2')) {
        Write-Host ''
        if (Read-YesNo 'Do you use REPENTOGON?' $true) {
            $launcherExe = Find-RepentogonLauncher $gameDir
            if ($launcherExe) {
                Write-Ok "Launcher: $launcherExe"
                if (-not (Read-YesNo 'Use this one?')) { $launcherExe = $null }
            }
            while (-not $launcherExe) {
                Write-Info 'REPENTOGONLauncher.exe lives OUTSIDE the game folder.'
                $picked = Select-FilePath 'Select REPENTOGONLauncher.exe' `
                          'REPENTOGON Launcher|REPENTOGONLauncher.exe|All executables (*.exe)|*.exe' `
                          (Split-Path $gameDir -Parent)
                $launcherExe = Resolve-LauncherPath $picked
                if (-not $launcherExe -and -not (Read-YesNo 'Try again?')) { break }
            }
        }
    }

    if ($ownsOnSteam) {
        Write-Head 'Steam launch options'
        if ($launcherExe) {
            Write-Info 'To make Steam start REPENTOGON (also required for Remote Play):'
            Write-Host ''
            Write-Host '  Steam > The Binding of Isaac: Rebirth > gear icon > Properties'
            Write-Host '  > General > Launch Options, paste exactly:'
            Write-Host ''
            Write-Host "    `"$launcherExe`" --isaac=%command%" -ForegroundColor White
            Write-Host ''
            Write-Info 'With that set, this tool can pick the build per profile.'
            $perProfileBuild = Read-YesNo 'Choose REPENTOGON vs vanilla per profile?' $true
        } else {
            Write-Info 'Launch from Steam as normal. Every profile runs vanilla.'
        }
    } elseif ($launcherExe) {
        Write-Info 'Launch from REPENTOGONLauncher.exe as normal.'
        $perProfileBuild = Read-YesNo 'Choose REPENTOGON vs vanilla per profile?' $true
    }

    $useRgon = @()
    if ($perProfileBuild) {
        Write-Host ''
        Write-Warn 'Anyone playing online together must be on the SAME build.'
        Write-Warn 'REPENTOGON is J273; vanilla is newer. Mixed builds desync at frame 1.'
        Write-Host ''
        foreach ($p in $profileNames) {
            if (Read-YesNo "  Run '$p' with REPENTOGON?" $false) { $useRgon += $p }
        }
    }

    # --- Shortcuts ---------------------------------------------------------
    Write-Head 'Step 6 of 6: Shortcuts'
    Write-Info 'One shortcut per profile. Double-click to switch, then launch the game.'
    Write-Host ''
    Write-Host '    [1] Desktop'
    Write-Host '    [2] Start Menu'
    Write-Host '    [3] Both'
    Write-Host '    [4] A folder I choose'
    Write-Host '    [5] None'
    Write-Host ''
    $shortcutDirs = @()
    switch (Read-Default 'Choice' '1') {
        '1' { $shortcutDirs = @([Environment]::GetFolderPath('Desktop')) }
        '2' { $shortcutDirs = @((Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs')) }
        '3' { $shortcutDirs = @([Environment]::GetFolderPath('Desktop'),
                                (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs')) }
        '4' {
            $custom = Select-FolderPath 'Where should the shortcuts go?' ([Environment]::GetFolderPath('Desktop'))
            if ($custom) {
                if (-not (Test-Path -LiteralPath $custom)) { New-Item -ItemType Directory -Path $custom -Force | Out-Null }
                $shortcutDirs = @($custom)
            }
        }
        default { Write-Info 'Skipped.' }
    }

    # --- Activate first profile --------------------------------------------
    Write-Head 'Activating a profile'
    $startProfile = Read-Default "Which profile should be active now? ($($profileNames -join '/'))" $profileNames[0]
    if ($profileNames -notcontains $startProfile) { $startProfile = $profileNames[0] }

    if (Test-IsJunction $modsDir) {
        Remove-JunctionLink $modsDir
        Write-Ok 'Removed old junction (target untouched)'
    } elseif (Test-Path -LiteralPath $modsDir) {
        # Rename, never delete. If anything above went wrong, originals survive.
        $backup = Join-Path $gameDir ("mods.backup-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
        Move-Item -LiteralPath $modsDir -Destination $backup
        Write-Ok "Original mods folder preserved as '$(Split-Path $backup -Leaf)'"
        Write-Info 'Delete it yourself once you have confirmed everything works.'
    }
    New-ProfileJunction -LinkPath $modsDir -TargetPath (Join-Path $syncRoot $startProfile)
    Write-Ok "mods\ -> $syncRoot\$startProfile"

    # --- Save ---------------------------------------------------------------
    $config = [pscustomobject]@{
        ConfigVersion  = 3
        IsaacExe       = $exe
        GameDir        = $gameDir
        ModsDir        = $modsDir
        SyncRoot       = $syncRoot
        Profiles       = $profileNames
        ActiveProfile  = $startProfile
        UseRepentogon  = $useRgon
        PerProfileBuild= $perProfileBuild
        LauncherExe    = $launcherExe
        OwnsOnSteam    = $ownsOnSteam
        ShortcutDirs   = $shortcutDirs
        SetupDate      = (Get-Date -Format 'o')
    }
    Save-Config $config
    Write-Ok "Config saved to $Script:ConfigPath"

    Write-Host ''
    foreach ($p in $profileNames) { Publish-Profile $config $p }

    # --- Sharing ------------------------------------------------------------
    Write-Head 'Sharing profiles with other people'
    Write-Host ''
    Write-Host '    [1] Set up Syncthing (live, automatic, deletions propagate)'
    Write-Host '    [2] Just show me the repo to download from'
    Write-Host '    [3] Neither'
    Write-Host ''
    switch (Read-Default 'Choice' '1') {
        '1' {
            $st = Get-SyncthingInfo
            if (-not $st) {
                Write-Info 'Syncthing does not appear to be installed.'
                Write-Host ''
                Write-Host '    https://syncthing.net/downloads/'
                Write-Host '    https://github.com/canton7/SyncTrayzor/releases/latest  (easier on Windows)'
                Write-Host ''
                if (Read-YesNo 'Open the download page now?') { Start-Process 'https://syncthing.net/downloads/' }
            } else {
                Write-Ok 'Syncthing is already installed.'
                Write-Host "     GUI address : http://$($st.Address)"
                Write-Host "     API key     : $($st.ApiKey)"
                if (Read-YesNo 'Open the Syncthing web GUI now?') { Start-Process "http://$($st.Address)" }
            }
            Write-Host ''
            Write-Host '  In Syncthing: Add Folder'
            Write-Host "    Folder Path:  $syncRoot"
            Write-Host '    Share it, then set THEIR side to "Receive Only" and yours to'
            Write-Host '    "Send Only" so your deletions reach them and their local'
            Write-Host '    experiments never travel back.'
        }
        '2' {
            Write-Host "  $Script:RepoUrl"
            Write-Host ''
            Write-Host "  Clone or download into:  $syncRoot"
        }
        default { Write-Info 'Skipped.' }
    }

    Write-Head 'Done'
    Write-Host "  Active profile: $startProfile" -ForegroundColor White
    Write-Host '  Switch with the shortcuts, then launch Isaac however you normally do.'
    Write-Host ''
    Write-Warn 'For online play everyone needs IDENTICAL profile contents - same files,'
    Write-Warn 'not just the same names - and the same game build.'
    Write-Host ''
}

# ---------------------------------------------------------------------------
# Commands
# ---------------------------------------------------------------------------

function Assert-Config {
    $config = Get-Config
    if (-not $config) { Write-Err 'No config found. Run Setup.bat first.'; exit 1 }
    if (-not $config.PSObject.Properties['ConfigVersion'] -or [int]$config.ConfigVersion -lt 3) {
        Write-Err 'Your isaac-profiles.json was written by an older version.'
        Write-Err 'Run Setup.bat again to regenerate it.'
        exit 1
    }
    return $config
}

function Invoke-Use {
    param([string]$Name)
    $config = Assert-Config
    if ($config.Profiles -notcontains $Name) {
        Write-Err "Unknown profile '$Name'. Known: $($config.Profiles -join ', ')"
        exit 1
    }
    $target = Join-Path $config.SyncRoot $Name
    if (-not (Test-Path -LiteralPath $target)) { Write-Err "Profile folder missing: $target"; exit 1 }

    $modsDir = $config.ModsDir
    if (Test-Path -LiteralPath $modsDir) {
        if (Test-IsJunction $modsDir) {
            Remove-JunctionLink $modsDir
        } else {
            Write-Err "'$modsDir' is a real folder, not a junction."
            Write-Err 'Refusing to touch it. Move it aside, then re-run.'
            exit 1
        }
    }

    Remove-DisableMarkers $target | Out-Null
    New-ProfileJunction -LinkPath $modsDir -TargetPath $target

    $count = @(Get-ChildItem -LiteralPath $target -Directory -ErrorAction SilentlyContinue).Count
    Write-Host ''
    Write-Ok "Active profile: $Name  ($count mods)"

    if ($config.PerProfileBuild) {
        $wantRgon = ($config.UseRepentogon -and $config.UseRepentogon -contains $Name)
        if (Set-LauncherMode $(if ($wantRgon) { 1 } else { 0 })) {
            Write-Ok $(if ($wantRgon) { 'Build: REPENTOGON' } else { 'Build: vanilla' })
        }
    }

    $config.ActiveProfile = $Name
    Save-Config $config

    Write-Info $(if ($config.OwnsOnSteam) { 'Now launch Isaac from Steam.' } else { 'Now launch Isaac as usual.' })
    Write-Host ''
}

function Invoke-List {
    $config = Assert-Config
    Write-Head 'Isaac mod profiles'
    foreach ($p in $config.Profiles) {
        $dir = Join-Path $config.SyncRoot $p
        $n = @(Get-ChildItem -LiteralPath $dir -Directory -ErrorAction SilentlyContinue).Count
        $mark = if ($p -eq $config.ActiveProfile) { '*' } else { ' ' }
        $build = ''
        if ($config.PerProfileBuild) {
            $build = if ($config.UseRepentogon -contains $p) { '  [REPENTOGON]' } else { '  [vanilla]' }
        }
        Write-Host ("   {0} {1,-24} {2,3} mods{3}" -f $mark, $p, $n, $build)
    }
    Write-Host ''
    Write-Info "* = active.  Folder: $($config.SyncRoot)"
    Write-Host ''
}

function Invoke-Add {
    param([string]$Name)
    $config = Assert-Config
    if (-not (Test-ProfileName $Name)) { Write-Err "Invalid profile name '$Name'."; exit 1 }
    if ($config.Profiles -contains $Name) { Write-Err "Profile '$Name' already exists."; exit 1 }

    $dir = Join-Path $config.SyncRoot $Name
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

    $config.Profiles = @($config.Profiles) + $Name

    if ($config.PerProfileBuild) {
        if (Read-YesNo "Run '$Name' with REPENTOGON?" $false) {
            $config.UseRepentogon = @($config.UseRepentogon) + $Name
        }
    }

    # Offer to seed from an existing profile - copying then pruning is usually
    # faster than assembling a set from nothing.
    if ($config.Profiles.Count -gt 1) {
        Write-Host ''
        if (Read-YesNo 'Copy mods from an existing profile as a starting point?' $false) {
            $src = Read-Default "Copy from which? ($(($config.Profiles | Where-Object { $_ -ne $Name }) -join '/'))" $config.ActiveProfile
            $srcDir = Join-Path $config.SyncRoot $src
            if (Test-Path -LiteralPath $srcDir) {
                Get-ChildItem -LiteralPath $srcDir -Directory | ForEach-Object {
                    Copy-Item -LiteralPath $_.FullName -Destination $dir -Recurse -Force
                }
                Remove-DisableMarkers $dir | Out-Null
                Write-Ok "Copied from '$src'"
            } else { Write-Warn "'$src' not found - left empty." }
        }
    }

    Save-Config $config
    Write-Ok "Created profile '$Name'  ($dir)"
    Publish-Profile $config $Name
    Write-Host ''
}

function Invoke-Remove {
    param([string]$Name)
    $config = Assert-Config
    if ($config.Profiles -notcontains $Name) { Write-Err "Unknown profile '$Name'."; exit 1 }
    if ($config.ActiveProfile -eq $Name) {
        Write-Err "'$Name' is active. Switch to another profile first."
        exit 1
    }
    $config.Profiles      = @($config.Profiles | Where-Object { $_ -ne $Name })
    $config.UseRepentogon = @($config.UseRepentogon | Where-Object { $_ -ne $Name })
    Save-Config $config

    # Remove generated launchers, but never the mod folder itself.
    Remove-Item -LiteralPath (Join-Path $PSScriptRoot "Switch to $Name.bat") -Force -ErrorAction SilentlyContinue
    foreach ($dir in @($config.ShortcutDirs)) {
        if ($dir) { Remove-Item -LiteralPath (Join-Path $dir "Isaac profile - $Name.lnk") -Force -ErrorAction SilentlyContinue }
    }

    Write-Ok "Removed '$Name' from the profile list."
    Write-Info "Its folder is untouched: $(Join-Path $config.SyncRoot $Name)"
    Write-Host ''
}

# ---------------------------------------------------------------------------

try {
    if     ($Setup)   { Invoke-Setup }
    elseif ($Use)     { Invoke-Use $Use }
    elseif ($Add)     { Invoke-Add $Add }
    elseif ($Remove)  { Invoke-Remove $Remove }
    elseif ($List)    { Invoke-List }
    elseif (-not (Test-Path -LiteralPath $Script:ConfigPath)) { Invoke-Setup }
    else {
        Invoke-List
        Write-Host '  Switch :  .\IsaacProfiles.ps1 -Use <name>'
        Write-Host '  Add    :  .\IsaacProfiles.ps1 -Add <name>'
        Write-Host '  Remove :  .\IsaacProfiles.ps1 -Remove <name>'
        Write-Host '  Redo   :  .\IsaacProfiles.ps1 -Setup'
        Write-Host ''
    }
} catch {
    Write-Err $_.Exception.Message
    Write-Host ''
    exit 1
}
