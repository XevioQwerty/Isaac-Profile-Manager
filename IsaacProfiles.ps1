<#
.SYNOPSIS
    Isaac Mod Profile Manager - switch between mod sets using a directory junction.

.DESCRIPTION
    Keeps multiple Isaac mod sets in one synced folder and points the game's
    mods\ directory at whichever one you want via a junction. Nothing is copied
    at launch, so switching is instant and the synced folder is the only truth.

    Run with -Setup for the first-time wizard. After that the generated
    "Play Online.bat" / "Play Singleplayer.bat" do the switching.

.PARAMETER Setup
    Run the first-time setup wizard.

.PARAMETER ProfileName
    Which profile to activate (any folder name under the sync root).

.PARAMETER NoLaunch
    Switch the profile but do not start the game.

.EXAMPLE
    .\IsaacProfiles.ps1 -Setup
    .\IsaacProfiles.ps1 -ProfileName online
#>

[CmdletBinding()]
param(
    [switch]$Setup,
    [string]$ProfileName,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# EDIT ME before sharing: your repo, shown to users who skip Syncthing.
# ---------------------------------------------------------------------------
$Script:RepoUrl = 'https://github.com/XevioQwerty/IsaacSync'

$Script:ConfigPath = Join-Path $PSScriptRoot 'isaac-profiles.json'
$Script:DefaultProfiles = @('online', 'singleplayer')

# ---------------------------------------------------------------------------
# Output helpers
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
# Junction handling
#
# These are the dangerous operations. A junction looks like a real folder to
# most tools, and Remove-Item -Recurse on one has historically followed the
# link and deleted the TARGET's contents. Everything below deletes links only,
# via the .NET call that cannot recurse.
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
    return $null   # older PowerShell doesn't expose .Target; display-only, so fine
}

function Remove-JunctionLink {
    param([string]$Path)
    if (-not (Test-IsJunction $Path)) {
        throw "Refusing to delete '$Path' - it is not a junction. Move it aside manually."
    }
    # recursive:$false - removes the link, never touches the target
    [System.IO.Directory]::Delete($Path, $false)
}

function New-ProfileJunction {
    param([string]$LinkPath, [string]$TargetPath)
    if (-not (Test-Path -LiteralPath $TargetPath)) {
        throw "Junction target does not exist: $TargetPath"
    }
    New-Item -ItemType Junction -Path $LinkPath -Target $TargetPath -Force | Out-Null
}

# ---------------------------------------------------------------------------
# Discovery
# ---------------------------------------------------------------------------

function Get-DocumentsPath {
    # Do not use $env:USERPROFILE\Documents - OneDrive redirection breaks it.
    return [Environment]::GetFolderPath('MyDocuments')
}

function Get-LauncherIniPath {
    return Join-Path (Get-DocumentsPath) 'My Games\repentogon_launcher.ini'
}

function Find-IsaacExe {
    # 1. REPENTOGON launcher already knows the path - best source.
    $ini = Get-LauncherIniPath
    if (Test-Path -LiteralPath $ini) {
        $match = Select-String -LiteralPath $ini -Pattern '^\s*IsaacExecutable\s*=\s*(.+?)\s*$' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($match) {
            $candidate = $match.Matches[0].Groups[1].Value.Trim()
            if (Test-Path -LiteralPath $candidate) {
                Write-Info "Found game path in repentogon_launcher.ini"
                return $candidate
            }
        }
    }

    # 2. Common install locations.
    $guesses = @()
    foreach ($drive in (Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Free -ne $null })) {
        $guesses += Join-Path $drive.Root 'Program Files (x86)\Steam\steamapps\common\The Binding of Isaac Rebirth\isaac-ng.exe'
        $guesses += Join-Path $drive.Root 'Steam\steamapps\common\The Binding of Isaac Rebirth\isaac-ng.exe'
        $guesses += Join-Path $drive.Root 'SteamLibrary\steamapps\common\The Binding of Isaac Rebirth\isaac-ng.exe'
    }
    foreach ($g in $guesses) {
        if (Test-Path -LiteralPath $g) { Write-Info "Found game at a standard Steam path"; return $g }
    }
    return $null
}

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

function Find-RepentogonLauncher {
    # The launcher must NOT live inside the game install - the official docs
    # warn against extracting it there, and specifically against a folder named
    # "repentogon" in the game dir since that name is used by the downgraded
    # build. So look beside the install and in common standalone locations,
    # then fall back to asking.
    param([string]$GameDir)
    $parent = Split-Path $GameDir -Parent
    $roots = @($parent) + @(Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue |
                            ForEach-Object { $_.Root })
    foreach ($root in ($roots | Select-Object -Unique)) {
        foreach ($name in @('REPENTOGONLauncher', 'Repentogon', 'RepentogonLauncher')) {
            $candidate = Join-Path $root "$name\REPENTOGONLauncher.exe"
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
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
    # Accept either the exe itself or the folder containing it.
    # NOTE: parameter is not named $Input - that is a PowerShell automatic variable.
    param([string]$PathText)
    if ([string]::IsNullOrWhiteSpace($PathText)) { return $null }
    $p = $PathText.Trim().Trim('"')
    if (Test-Path -LiteralPath $p -PathType Leaf) {
        if ([IO.Path]::GetFileName($p) -ieq 'REPENTOGONLauncher.exe') { return $p }
        return $null
    }
    if (Test-Path -LiteralPath $p -PathType Container) {
        $candidate = Join-Path $p 'REPENTOGONLauncher.exe'
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    return $null
}

function Remove-DisableMarkers {
    param([string]$ProfileDir)
    $markers = @(Get-ChildItem -LiteralPath $ProfileDir -Recurse -Filter 'disable.it' -Force -ErrorAction SilentlyContinue)
    if ($markers.Count -gt 0) {
        $markers | Remove-Item -Force -ErrorAction SilentlyContinue
        Write-Info "Cleared $($markers.Count) disable.it marker(s) - folder contents are the mod list"
    }
}

function New-Shortcut {
    param([string]$TargetPath, [string]$ShortcutPath, [string]$IconPath, [string]$WorkDir)
    try {
        $shell = New-Object -ComObject WScript.Shell
        $lnk = $shell.CreateShortcut($ShortcutPath)
        $lnk.TargetPath       = $TargetPath
        $lnk.WorkingDirectory = $WorkDir
        $lnk.Description      = 'Isaac Mod Profile Manager'
        if ($IconPath -and (Test-Path -LiteralPath $IconPath)) { $lnk.IconLocation = "$IconPath,0" }
        $lnk.Save()
        return $true
    } catch {
        Write-Warn "Could not create shortcut: $($_.Exception.Message)"
        return $false
    }
}

function Select-IsaacExe {
    return (Select-FilePath 'Select isaac-ng.exe' `
                            'Isaac executable (isaac-ng.exe)|isaac-ng.exe|All executables (*.exe)|*.exe' `
                            $null)
}

function Get-SyncthingInfo {
    # Read-only. Surfaces the API key and GUI address so manual setup is copy-paste.
    $cfg = Join-Path $env:LOCALAPPDATA 'Syncthing\config.xml'
    if (-not (Test-Path -LiteralPath $cfg)) {
        $cfg = Join-Path $env:APPDATA 'Syncthing\config.xml'
    }
    if (-not (Test-Path -LiteralPath $cfg)) { return $null }
    try {
        [xml]$xml = Get-Content -LiteralPath $cfg -Raw
        return [pscustomobject]@{
            ConfigPath = $cfg
            ApiKey     = $xml.configuration.gui.apikey
            Address    = $xml.configuration.gui.address
        }
    } catch { return $null }
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
    $Config | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $Script:ConfigPath -Encoding UTF8
}

# ---------------------------------------------------------------------------
# Launcher mode
#
# repentogon_launcher.ini has [Shared] LaunchMode. Which number means what is
# NOT documented anywhere - it was inferred from one install. So setup records
# the value observed in each mode rather than assuming, and this is opt-in.
# ---------------------------------------------------------------------------

function Set-LauncherMode {
    param([int]$Mode)
    $ini = Get-LauncherIniPath
    if (-not (Test-Path -LiteralPath $ini)) { Write-Info 'No launcher ini found - skipping mode switch.'; return }
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
    if ($inShared -and -not $written) { $out += "LaunchMode = $Mode" }
    Set-Content -LiteralPath $ini -Value $out -Encoding UTF8
    Write-Ok "Launcher mode set to $Mode"
}

# ---------------------------------------------------------------------------
# Generated launch scripts
# ---------------------------------------------------------------------------

function Write-PlayScripts {
    param($Config)
    foreach ($name in $Config.Profiles) {
        $title = (Get-Culture).TextInfo.ToTitleCase($name)
        $file  = Join-Path $PSScriptRoot "Play $title.bat"
        $body  = @"
@echo off
REM Generated by IsaacProfiles.ps1 - safe to delete and regenerate.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0IsaacProfiles.ps1" -ProfileName "$name"
if errorlevel 1 pause
"@
        Set-Content -LiteralPath $file -Value $body -Encoding ASCII
        Write-Ok "Wrote '$(Split-Path $file -Leaf)'"
    }
}

# ---------------------------------------------------------------------------
# Setup wizard
# ---------------------------------------------------------------------------

function Invoke-Setup {
    Write-Host ''
    Write-Host '  ===============================================' -ForegroundColor Cyan
    Write-Host '   Isaac Mod Profile Manager - first-time setup' -ForegroundColor Cyan
    Write-Host '  ===============================================' -ForegroundColor Cyan

    # --- Step 1: locate the game ------------------------------------------
    Write-Head 'Step 1 of 5: Locate the game'
    $exe = Find-IsaacExe
    if ($exe) {
        Write-Host "  Detected: $exe" -ForegroundColor White
        if (-not (Read-YesNo 'Use this installation?')) { $exe = $null }
    }
    if (-not $exe) {
        Write-Info 'Pick your isaac-ng.exe...'
        $exe = Select-IsaacExe
    }
    if (-not $exe -or -not (Test-Path -LiteralPath $exe)) { Write-Err 'No valid executable selected. Aborting.'; return }

    $gameDir = Split-Path -Parent $exe
    $modsDir = Join-Path $gameDir 'mods'
    Write-Ok "Game directory: $gameDir"

    # --- Step 2: sync root -------------------------------------------------
    Write-Head 'Step 2 of 5: Choose the synced folder'
    Write-Info 'This folder holds every mod set and is what Syncthing/git will track.'
    Write-Info 'Keep it OUTSIDE the game directory.'
    $gameDrive   = [IO.Path]::GetPathRoot($gameDir)
    $defaultRoot = Join-Path $gameDrive 'IsaacSync'
    $syncRoot    = Select-FolderPath 'Choose the folder to hold your mod sets' $defaultRoot
    if ([string]::IsNullOrWhiteSpace($syncRoot)) { Write-Err 'No folder chosen. Aborting.'; return }
    if (-not (Test-Path -LiteralPath $syncRoot)) { New-Item -ItemType Directory -Path $syncRoot -Force | Out-Null }
    Write-Ok "Sync folder: $syncRoot"

    if ($syncRoot.TrimEnd('\').ToLower().StartsWith($gameDir.TrimEnd('\').ToLower())) {
        Write-Err 'That path is inside the game directory. Pick somewhere else.'
        return
    }

    $profiles = @()
    Write-Info "Default profiles: $($Script:DefaultProfiles -join ', ')"
    if (Read-YesNo 'Use these two profiles?') {
        $profiles = $Script:DefaultProfiles
    } else {
        $raw = Read-Default 'Profile names (comma separated)' ($Script:DefaultProfiles -join ',')
        $profiles = @($raw -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    }
    if ($profiles.Count -eq 0) { Write-Err 'No profile names given. Aborting.'; return }

    foreach ($p in $profiles) {
        $dir = Join-Path $syncRoot $p
        if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        Write-Ok "Profile folder ready: $dir"
    }

    # Keep sync/VCS metadata above the junction target so Isaac never enumerates it.
    # These two files must each exclude the OTHER tool's metadata, or git will
    # track Syncthing's marker dirs and Syncthing will replicate a live .git
    # (which causes index/lock collisions and can corrupt the repo).
    $stignore = Join-Path $syncRoot '.stignore'
    if (-not (Test-Path -LiteralPath $stignore)) {
        Set-Content -LiteralPath $stignore -Encoding UTF8 -Value @(
            '// Never let Syncthing replicate a live git directory.'
            '.git'
            '.gitignore'
            '.gitattributes'
            '.stversions'
            'README.md'
        )
        Write-Ok 'Wrote .stignore'
    }
    $gitignore = Join-Path $syncRoot '.gitignore'
    if (-not (Test-Path -LiteralPath $gitignore)) {
        Set-Content -LiteralPath $gitignore -Encoding UTF8 -Value @(
            '# Syncthing metadata - machine-local, never commit'
            '.stfolder/'
            '.stversions/'
            '.stignore'
            '.syncthing.*.tmp'
            ''
            '# This tool''s local state'
            'isaac-profiles.json'
        )
        Write-Ok 'Wrote .gitignore'
    }
    $gitattr = Join-Path $syncRoot '.gitattributes'
    if (-not (Test-Path -LiteralPath $gitattr)) {
        Set-Content -LiteralPath $gitattr -Encoding UTF8 -Value @(
            '# Byte-for-byte identical checkouts. Without this, git rewrites line'
            '# endings in .lua/.xml and a cloned copy differs from a synced one.'
            '* -text'
        )
        Write-Ok 'Wrote .gitattributes'
    }

    # --- Step 3: migrate existing mods -------------------------------------
    Write-Head 'Step 3 of 5: Migrate your current mods'
    $migrateInto = $null
    if (Test-IsJunction $modsDir) {
        $existingTarget = Get-JunctionTarget $modsDir
        Write-Info "mods\ is already a junction$(if($existingTarget){" -> $existingTarget"})."
        Write-Info 'Nothing to migrate. Setup will re-point it at the end.'
    } elseif (Test-Path -LiteralPath $modsDir) {
        $folders = @(Get-ChildItem -LiteralPath $modsDir -Directory -ErrorAction SilentlyContinue)
        Write-Info "Found $($folders.Count) mod folder(s) in the game's mods directory."
        if ($folders.Count -gt 0) {
            Write-Host ''
            for ($i = 0; $i -lt $profiles.Count; $i++) { Write-Host "    [$($i+1)] copy into '$($profiles[$i])'" }
            Write-Host "    [a] copy into ALL profiles (prune later)"
            Write-Host "    [s] skip - I'll populate the folders myself"
            Write-Host ''
            $choice = Read-Default 'Choice' 'a'
            if ($choice -eq 'a') { $migrateInto = $profiles }
            elseif ($choice -eq 's') { $migrateInto = @() }
            elseif ($choice -match '^\d+$' -and [int]$choice -ge 1 -and [int]$choice -le $profiles.Count) {
                $migrateInto = @($profiles[[int]$choice - 1])
            } else { $migrateInto = $profiles }

            foreach ($p in $migrateInto) {
                $dest = Join-Path $syncRoot $p
                Write-Info "Copying $($folders.Count) folders into '$p'..."
                foreach ($f in $folders) {
                    Copy-Item -LiteralPath $f.FullName -Destination $dest -Recurse -Force
                }
                Write-Ok "Populated '$p'"
            }
        }
    } else {
        Write-Info 'No mods directory yet - it will be created as a junction.'
    }

    # --- Step 4: create the junction ---------------------------------------
    Write-Head 'Step 4 of 5: Point mods\ at a profile'
    $startProfile = Read-Default "Which profile should be active now? ($($profiles -join '/'))" $profiles[0]
    if ($profiles -notcontains $startProfile) { Write-Err "Unknown profile '$startProfile'."; return }

    if (Test-IsJunction $modsDir) {
        Remove-JunctionLink $modsDir
        Write-Ok 'Removed old junction (target untouched)'
    } elseif (Test-Path -LiteralPath $modsDir) {
        # Rename rather than delete. If anything went wrong above, the originals survive.
        $backup = Join-Path $gameDir ("mods.backup-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
        Move-Item -LiteralPath $modsDir -Destination $backup
        Write-Ok "Original mods folder preserved as '$(Split-Path $backup -Leaf)'"
        Write-Info 'Delete it yourself once you have confirmed everything works.'
    }

    New-ProfileJunction -LinkPath $modsDir -TargetPath (Join-Path $syncRoot $startProfile)
    Write-Ok "mods\ -> $syncRoot\$startProfile"

    # --- Clear inherited disable.it markers --------------------------------
    # Migrated folders almost always carry these from whenever the mod was last
    # switched off. A mod can then be present but silently disabled, which looks
    # exactly like the sync having failed. Always clear on first setup.
    Write-Head 'Clearing stale disable.it markers'
    $totalCleared = 0
    foreach ($p in $profiles) {
        $dir = Join-Path $syncRoot $p
        $markers = @(Get-ChildItem -LiteralPath $dir -Recurse -Filter 'disable.it' -Force -ErrorAction SilentlyContinue)
        if ($markers.Count -gt 0) {
            $markers | Remove-Item -Force -ErrorAction SilentlyContinue
            Write-Ok "'$p': cleared $($markers.Count) marker(s)"
            $totalCleared += $markers.Count
        }
    }
    if ($totalCleared -eq 0) { Write-Info 'None found - nothing to clear.' }
    else { Write-Info 'Those mods are now enabled. Remove folders you do not want.' }

    # --- Which build does each profile run? --------------------------------
    Write-Head 'REPENTOGON per profile'
    $rgonExe = Join-Path $gameDir 'Repentogon\isaac-ng.exe'
    $hasRgon = Test-Path -LiteralPath $rgonExe
    if ($hasRgon) {
        Write-Ok "Found REPENTOGON build: $rgonExe"
        Write-Info 'REPENTOGON runs J273. The vanilla exe is a newer J-version.'
    } else {
        Write-Info 'No Repentogon subfolder found - every profile will run vanilla.'
    }

    # Identify the multiplayer profile FIRST. It is then locked to vanilla and
    # never offered a REPENTOGON choice - mixed builds desync instantly, and a
    # stray keystroke here would be invisible until someone gets dropped.
    Write-Host ''
    Write-Info 'Which profile do you use for online play with other people?'
    Write-Info "Options: $($profiles -join ', ')  (or 'none')"
    $onlineProfile = Read-Default 'Online profile' $(if ($profiles -contains 'online') { 'online' } else { 'none' })
    if ($onlineProfile -ne 'none' -and $profiles -notcontains $onlineProfile) {
        Write-Warn "'$onlineProfile' is not a profile - treating as 'none'."
        $onlineProfile = 'none'
    }
    if ($onlineProfile -ne 'none') {
        Write-Ok "'$onlineProfile' is locked to the vanilla build."
        Write-Info 'Every player must be on the same build. This is not configurable.'
    }

    $useRgon = @()
    if ($hasRgon) {
        foreach ($p in $profiles) {
            if ($p -eq $onlineProfile) { continue }
            if (Read-YesNo "  Run '$p' with REPENTOGON?" $true) { $useRgon += $p }
        }
    }

    # The REPENTOGON build refuses to be started directly - it pops
    # "This exe should only be launched using the REPENTOGONLauncher" and exits.
    # So those profiles must go through the launcher, passing the VANILLA exe
    # via --isaac= exactly as the official Steam launch-option docs describe.
    $launcherExe = $null
    $rgonMode = 1   # 1 = REPENTOGON on, 0 = off (--repentogonoff). Confirmed on two installs.
    if ($useRgon.Count -gt 0) {
        Write-Host ''
        $launcherExe = Find-RepentogonLauncher $gameDir
        if ($launcherExe) {
            Write-Ok "Launcher: $launcherExe"
            if (-not (Read-YesNo 'Use this one?')) { $launcherExe = $null }
        }
        while (-not $launcherExe) {
            Write-Info 'REPENTOGONLauncher.exe is required for those profiles.'
            Write-Info 'It lives OUTSIDE the game folder - wherever you extracted it.'
            $picked = Select-FilePath 'Select REPENTOGONLauncher.exe' `
                                      'REPENTOGON Launcher|REPENTOGONLauncher.exe|All executables (*.exe)|*.exe' `
                                      (Split-Path $gameDir -Parent)
            $launcherExe = Resolve-LauncherPath $picked
            if (-not $launcherExe) {
                Write-Warn 'That is not REPENTOGONLauncher.exe (a folder containing it is fine too).'
                if (-not (Read-YesNo 'Try again?')) {
                    Write-Warn 'No launcher - those profiles will fall back to vanilla.'
                    $useRgon = @()
                    break
                }
            }
        }
    }

    $modeMap = @{}

    # --- disable.it handling -----------------------------------------------
    Write-Head 'Optional: ignore disable.it markers'
    Write-Info 'Mods carry a disable.it file when switched off. Migrated folders often'
    Write-Info 'still have them, so a mod can be present but silently disabled.'
    Write-Info 'Clearing them makes folder contents the only thing that matters -'
    Write-Info 'strongly recommended for any profile used online.'
    $stripList = @()
    foreach ($p in $profiles) {
        if ($p -eq $onlineProfile) {
            $stripList += $p
            Write-Ok "'$p': always cleared (required for online parity)"
            continue
        }
        if (Read-YesNo "  Clear disable.it markers in '$p' on every switch?" $true) {
            $stripList += $p
        }
    }

    # --- Step 5: save + generate -------------------------------------------
    Write-Head 'Step 5 of 5: Save configuration'
    $config = [pscustomobject]@{
        ConfigVersion  = 2
        IsaacExe       = $exe
        RepentogonExe  = $(if ($hasRgon) { $rgonExe } else { $null })
        LauncherExe    = $launcherExe
        UseRepentogon  = $useRgon
        RepentogonMode = $rgonMode
        OnlineProfile  = $onlineProfile
        GameDir        = $gameDir
        ModsDir        = $modsDir
        SyncRoot       = $syncRoot
        Profiles       = $profiles
        ActiveProfile  = $startProfile
        LaunchModeMap  = $modeMap
        StripDisableIt = $stripList
        LaunchArgs     = @('--luaheapsize=1024M')
        SetupDate      = (Get-Date -Format 'o')
    }
    Save-Config $config
    Write-Ok "Config saved to $Script:ConfigPath"
    Write-PlayScripts $config

    # --- Shortcuts ---------------------------------------------------------
    Write-Head 'Shortcuts'
    Write-Host ''
    Write-Host '    [1] Desktop'
    Write-Host '    [2] Start Menu'
    Write-Host '    [3] Both'
    Write-Host '    [4] A folder I choose'
    Write-Host '    [5] None'
    Write-Host ''
    $shortcutDirs = @()
    switch (Read-Default 'Where should the Play shortcuts go?' '1') {
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
    foreach ($dir in $shortcutDirs) {
        foreach ($name in $profiles) {
            $title = (Get-Culture).TextInfo.ToTitleCase($name)
            $bat   = Join-Path $PSScriptRoot "Play $title.bat"
            $lnk   = Join-Path $dir "Isaac - $title.lnk"
            if (New-Shortcut -TargetPath $bat -ShortcutPath $lnk -IconPath $exe -WorkDir $PSScriptRoot) {
                Write-Ok "Shortcut: $lnk"
            }
        }
    }

    # --- Syncing -----------------------------------------------------------
    Write-Head 'Sharing this with other people'
    Write-Host ''
    Write-Host '    [1] Set up Syncthing (live, automatic, handles deletions)'
    Write-Host '    [2] Just show me the repo to download from'
    Write-Host '    [3] Neither - I will sort it out myself'
    Write-Host ''
    switch (Read-Default 'Choice' '1') {
        '1' {
            Write-Head 'Syncthing setup'
            $st = Get-SyncthingInfo
            if (-not $st) {
                Write-Info 'Syncthing does not appear to be installed on this machine.'
                Write-Host ''
                Write-Host '    Official builds (always latest):'
                Write-Host '      https://syncthing.net/downloads/'
                Write-Host ''
                Write-Host '    SyncTrayzor - easier on Windows, adds a tray icon and'
                Write-Host '    starts with the machine:'
                Write-Host '      https://github.com/canton7/SyncTrayzor/releases/latest'
                Write-Host ''
                if (Read-YesNo 'Open the download page now?') {
                    Start-Process 'https://syncthing.net/downloads/'
                }
                Write-Info 'Install it, then follow the steps below.'
            } else {
                Write-Ok 'Syncthing is already installed.'
                Write-Host "     GUI address : http://$($st.Address)"
                Write-Host "     API key     : $($st.ApiKey)"
                Write-Host "     Config file : $($st.ConfigPath)"
                if (Read-YesNo 'Open the Syncthing web GUI now?') {
                    Start-Process "http://$($st.Address)"
                }
            }
            Write-Host ''
            Write-Host '  In the Syncthing GUI:'
            Write-Host '  1. Click "Add Folder"'
            Write-Host "  2. Folder Path:  $syncRoot"
            Write-Host '  3. Share it with the other people'
            Write-Host '  4. On THEIR machine set the folder type to "Receive Only" so'
            Write-Host '     their changes never overwrite yours. Yours stays "Send Only".'
            Write-Host '  5. Turn on File Versioning on their side for an undo.'
        }
        '2' {
            Write-Head 'Repository'
            Write-Host "  $Script:RepoUrl"
            Write-Host ''
            Write-Host '  Clone or download it directly into:'
            Write-Host "     $syncRoot"
            Write-Host '  Then re-run this setup and point it at that folder.'
        }
        default { Write-Info 'Skipped.' }
    }

    Write-Head 'Done'
    Write-Host "  Use 'Play Online.bat' / 'Play Singleplayer.bat' from now on." -ForegroundColor White
    Write-Host ''
    Write-Warn 'For online play, everyone must have IDENTICAL folder contents.'
    Write-Warn 'Not the same enabled list - the same files. Isaac desyncs otherwise.'
    Write-Host ''
}

# ---------------------------------------------------------------------------
# Profile switch
# ---------------------------------------------------------------------------

function Invoke-Switch {
    param([string]$Name)

    $config = Get-Config
    if (-not $config) { Write-Err 'No config found. Run with -Setup first.'; exit 1 }

    # A config written before build selection existed has no UseRepentogon key.
    # Falling back to vanilla silently would launch the wrong build, which on an
    # online session desyncs everyone. Stop instead.
    if (-not $config.PSObject.Properties['ConfigVersion'] -or [int]$config.ConfigVersion -lt 2) {
        Write-Err 'Your isaac-profiles.json was written by an older version.'
        Write-Err 'It has no REPENTOGON build selection, so this would launch the'
        Write-Err 'wrong executable. Run Setup.bat again to regenerate it.'
        exit 1
    }

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
            # Someone recreated a real mods folder. Never delete it silently.
            Write-Err "'$modsDir' is a real folder, not a junction."
            Write-Err 'Refusing to touch it. Move it aside, then re-run.'
            exit 1
        }
    }

    # Clear stale disable.it markers BEFORE the junction exists, so the game
    # never sees a half-toggled set.
    if ($config.StripDisableIt -and $config.StripDisableIt -contains $Name) {
        Remove-DisableMarkers $target
    }

    New-ProfileJunction -LinkPath $modsDir -TargetPath $target
    $count = @(Get-ChildItem -LiteralPath $target -Directory -ErrorAction SilentlyContinue).Count
    Write-Ok "Profile '$Name' active ($count mod folders)"

    if ($config.LaunchModeMap -and $config.LaunchModeMap.PSObject.Properties.Name -contains $Name) {
        Set-LauncherMode ([int]$config.LaunchModeMap.$Name)
    }

    $config.ActiveProfile = $Name
    Save-Config $config

    if (-not $NoLaunch) {
        # Launch the executable directly with the exact command line the
        # launcher itself uses. The launcher's [Shared] LaunchMode key is not
        # reliable to drive from outside - it gets rewritten on launcher exit -
        # so we bypass it entirely and pick the build ourselves.
        $wantRgon = ($config.UseRepentogon -and $config.UseRepentogon -contains $Name)

        # Belt and braces: even if someone hand-edits the json, the designated
        # online profile never runs a different build from everyone else.
        if ($config.OnlineProfile -and $Name -eq $config.OnlineProfile) {
            $wantRgon = $false
        }

        $launchArgs = @($config.LaunchArgs)

        if ($wantRgon) {
            if (-not ($config.LauncherExe -and (Test-Path -LiteralPath $config.LauncherExe))) {
                Write-Err "Profile '$Name' needs REPENTOGON, but REPENTOGONLauncher.exe"
                Write-Err 'is missing from the config. Re-run Setup.bat.'
                exit 1
            }
            # Point the launcher at the VANILLA exe. It resolves that to the
            # Repentogon build itself. Starting Repentogon\isaac-ng.exe directly
            # trips its "launch me through the launcher" guard and exits.
            Set-LauncherMode ([int]$config.RepentogonMode)
            $launcherDir = Split-Path $config.LauncherExe -Parent
            $rgonArgs = "--isaac=`"$($config.IsaacExe)`""
            Write-Ok 'Launching via REPENTOGON launcher'
            Write-Info "$($config.LauncherExe) $rgonArgs"
            Start-Process -FilePath $config.LauncherExe -ArgumentList $rgonArgs -WorkingDirectory $launcherDir
        } else {
            $exeToRun = $config.IsaacExe
            $launchArgs += '--repentogonoff'
            Write-Ok 'Launching vanilla build'
            Write-Info "$exeToRun $($launchArgs -join ' ')"
            Start-Process -FilePath $exeToRun -ArgumentList $launchArgs -WorkingDirectory $config.GameDir
        }

        Write-Host ''
        Write-Info 'Verify in log.txt: "Game Version:" must match on every player'
        Write-Info 'before an online session. J273 = REPENTOGON, newer = vanilla.'
    }
}

# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------

try {
    if ($Setup) { Invoke-Setup }
    elseif ($ProfileName) { Invoke-Switch $ProfileName }
    elseif (-not (Test-Path -LiteralPath $Script:ConfigPath)) { Invoke-Setup }
    else {
        $config = Get-Config
        Write-Head 'Isaac Mod Profile Manager'
        Write-Host "  Active profile : $($config.ActiveProfile)"
        Write-Host "  Sync root      : $($config.SyncRoot)"
        Write-Host "  Profiles       : $($config.Profiles -join ', ')"
        Write-Host ''
        Write-Host '  Switch with:  .\IsaacProfiles.ps1 -ProfileName <name>'
        Write-Host '  Reconfigure:  .\IsaacProfiles.ps1 -Setup'
        Write-Host ''
    }
} catch {
    Write-Err $_.Exception.Message
    Write-Host ''
    exit 1
}
