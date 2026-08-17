<#
.SYNOPSIS
    Posts a Discord embed when Isaac or REPENTOGON releases an update.

.DESCRIPTION
    Checks two sources and posts only things it hasn't seen before:
      * Steam announcements for The Binding of Isaac: Rebirth (appid 250900)
      * GitHub releases for TeamREPENTOGON/REPENTOGON

    Also reads your installed build from the game's log.txt so the embed can
    show what you're on versus what just came out.

.PARAMETER Setup
    Create the config file and optionally register a scheduled task.

.PARAMETER Test
    Post the latest item regardless of whether it's already been seen.

.EXAMPLE
    .\IsaacUpdateWatcher.ps1 -Setup
    .\IsaacUpdateWatcher.ps1
#>

[CmdletBinding()]
param(
    [switch]$Setup,
    [switch]$Test
)

$ErrorActionPreference = 'Stop'

$Script:ConfigPath = Join-Path $PSScriptRoot 'update-watcher.json'
$Script:StatePath  = Join-Path $PSScriptRoot 'update-watcher-state.json'
$Script:AppId      = 250900
$Script:RgonRepo   = 'TeamREPENTOGON/REPENTOGON'

# ---------------------------------------------------------------------------

function Get-InstalledVersion {
    # The log's "Game Version:" line is the reliable source for the J-number.
    # The exe's file version does not include it.
    param([string]$LogPath)
    if (-not $LogPath -or -not (Test-Path -LiteralPath $LogPath)) { return $null }
    try {
        $line = Select-String -LiteralPath $LogPath -Pattern 'Game Version:\s*(\S+)' |
                Select-Object -First 1
        if ($line) { return $line.Matches[0].Groups[1].Value }
    } catch { }
    return $null
}

function Send-DiscordEmbed {
    param(
        [string]$WebhookUrl,
        [string]$Title,
        [string]$Url,
        [string]$Description,
        [int]$Color,
        [string]$FooterText
    )
    $embed = @{
        title       = $Title
        color       = $Color
        timestamp   = (Get-Date).ToUniversalTime().ToString('o')
    }
    if ($Url)         { $embed.url = $Url }
    if ($Description) { $embed.description = $Description }
    if ($FooterText)  { $embed.footer = @{ text = $FooterText } }

    $payload = @{ embeds = @($embed) } | ConvertTo-Json -Depth 6

    # Discord requires UTF-8; PowerShell 5.1 would otherwise send latin-1
    # and mangle any non-ASCII characters in patch notes.
    $bytes = [Text.Encoding]::UTF8.GetBytes($payload)
    Invoke-RestMethod -Uri $WebhookUrl -Method Post -ContentType 'application/json; charset=utf-8' -Body $bytes | Out-Null
}

function Get-SteamNews {
    $url = "https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/?appid=$Script:AppId&count=10&maxlength=600&format=json"
    $resp = Invoke-RestMethod -Uri $url -Method Get -TimeoutSec 30
    return $resp.appnews.newsitems
}

function Get-RepentogonRelease {
    $url = "https://api.github.com/repos/$Script:RgonRepo/releases/latest"
    # GitHub rejects requests without a User-Agent.
    return Invoke-RestMethod -Uri $url -Method Get -TimeoutSec 30 -Headers @{
        'User-Agent' = 'IsaacUpdateWatcher'
        'Accept'     = 'application/vnd.github+json'
    }
}

function Convert-SteamText {
    # Steam announcements use BBCode. Strip the common tags so the embed reads
    # cleanly rather than showing [b]...[/b] everywhere.
    param([string]$Text)
    if (-not $Text) { return '' }
    $t = $Text -replace '\[/?(b|i|u|h\d|list|olist|quote|code|strike|spoiler|noparse)\]', ''
    $t = $t -replace '\[url=([^\]]+)\]([^\[]+)\[/url\]', '$2'
    $t = $t -replace '\[img\][^\[]*\[/img\]', ''
    $t = $t -replace '\[\*\]', "- "
    $t = $t -replace '\[[^\]]+\]', ''
    $t = $t -replace '(\r?\n){3,}', "`n`n"
    return $t.Trim()
}

function Get-State {
    if (Test-Path -LiteralPath $Script:StatePath) {
        try { return Get-Content -LiteralPath $Script:StatePath -Raw | ConvertFrom-Json } catch { }
    }
    return [pscustomobject]@{ SeenSteam = @(); SeenRgon = '' }
}

function Save-State {
    param($State)
    $State | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $Script:StatePath -Encoding UTF8
}

# ---------------------------------------------------------------------------

function Invoke-Setup {
    Write-Host ''
    Write-Host '  Isaac Update Watcher - setup' -ForegroundColor Cyan
    Write-Host ''
    Write-Host '  In Discord: Server Settings > Integrations > Webhooks > New Webhook'
    Write-Host '  Pick a channel, then Copy Webhook URL.'
    Write-Host ''
    $webhook = (Read-Host '  Paste the webhook URL').Trim()
    if ($webhook -notmatch '^https://discord(app)?\.com/api/webhooks/') {
        Write-Host '  [fail] That does not look like a Discord webhook URL.' -ForegroundColor Red
        return
    }

    $logDefault = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'My Games\Binding of Isaac Repentance+\log.txt'
    Write-Host ''
    Write-Host "  Game log (used to show your installed version in the embed)."
    $log = Read-Host "  Path [$logDefault]"
    if ([string]::IsNullOrWhiteSpace($log)) { $log = $logDefault }

    $cfg = [pscustomobject]@{
        WebhookUrl    = $webhook
        LogPath       = $log.Trim().Trim('"')
        WatchSteam    = $true
        WatchRepentogon = $true
    }
    $cfg | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $Script:ConfigPath -Encoding UTF8
    Write-Host "  [ok] Config saved: $Script:ConfigPath" -ForegroundColor Green
    Write-Host '  [info] Do NOT commit this file - the webhook URL is a secret.' -ForegroundColor Yellow

    Write-Host ''
    $ans = Read-Host '  Check automatically every 6 hours? [Y/n]'
    if ($ans -notmatch '^(n|no)$') {
        try {
            $action  = New-ScheduledTaskAction -Execute 'powershell.exe' `
                        -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$PSCommandPath`""
            $trigger = New-ScheduledTaskTrigger -Daily -At 9am
            $trigger.Repetition = (New-ScheduledTaskTrigger -Once -At 9am `
                        -RepetitionInterval (New-TimeSpan -Hours 6) `
                        -RepetitionDuration (New-TimeSpan -Days 1)).Repetition
            Register-ScheduledTask -TaskName 'Isaac Update Watcher' -Action $action -Trigger $trigger -Force | Out-Null
            Write-Host '  [ok] Scheduled task registered.' -ForegroundColor Green
        } catch {
            Write-Host "  [warn] Could not register task: $($_.Exception.Message)" -ForegroundColor Yellow
            Write-Host '  [info] Run this script manually, or add it to Task Scheduler yourself.'
        }
    }

    Write-Host ''
    Write-Host '  Run with -Test to post the latest item and confirm it works.'
    Write-Host ''
}

function Invoke-Check {
    if (-not (Test-Path -LiteralPath $Script:ConfigPath)) {
        Write-Host '  No config. Run with -Setup first.' -ForegroundColor Red
        exit 1
    }
    $cfg   = Get-Content -LiteralPath $Script:ConfigPath -Raw | ConvertFrom-Json
    $state = Get-State
    $installed = Get-InstalledVersion $cfg.LogPath
    $footer = if ($installed) { "You are running $installed" } else { $null }
    $posted = 0

    # --- Steam announcements ------------------------------------------------
    if ($cfg.WatchSteam) {
        try {
            $items = Get-SteamNews
            # Oldest first so a backlog posts in chronological order.
            foreach ($item in ($items | Sort-Object date)) {
                $seen = @($state.SeenSteam)
                if (-not $Test -and $seen -contains $item.gid) { continue }

                $body = Convert-SteamText $item.contents
                if ($body.Length -gt 1200) { $body = $body.Substring(0, 1200) + '...' }

                Send-DiscordEmbed -WebhookUrl $cfg.WebhookUrl `
                                  -Title $item.title `
                                  -Url $item.url `
                                  -Description $body `
                                  -Color 0xE8562B `
                                  -FooterText $footer
                $state.SeenSteam = @($seen + $item.gid | Select-Object -Last 60)
                $posted++
                Start-Sleep -Milliseconds 800   # stay clear of Discord rate limits
                if ($Test) { break }
            }
        } catch {
            Write-Host "  [warn] Steam check failed: $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }

    # --- REPENTOGON releases ------------------------------------------------
    if ($cfg.WatchRepentogon) {
        try {
            $rel = Get-RepentogonRelease
            if ($Test -or $rel.tag_name -ne $state.SeenRgon) {
                $notes = $rel.body
                if ($notes -and $notes.Length -gt 1200) { $notes = $notes.Substring(0, 1200) + '...' }
                Send-DiscordEmbed -WebhookUrl $cfg.WebhookUrl `
                                  -Title "REPENTOGON $($rel.tag_name)" `
                                  -Url $rel.html_url `
                                  -Description $notes `
                                  -Color 0x8B1A1A `
                                  -FooterText $footer
                $state.SeenRgon = $rel.tag_name
                $posted++
            }
        } catch {
            Write-Host "  [warn] REPENTOGON check failed: $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }

    Save-State $state
    Write-Host "  Done. Posted $posted item(s)." -ForegroundColor Green
}

# ---------------------------------------------------------------------------

try {
    if ($Setup) { Invoke-Setup }
    elseif (-not (Test-Path -LiteralPath $Script:ConfigPath)) { Invoke-Setup }
    else { Invoke-Check }
} catch {
    Write-Host "  [fail] $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
