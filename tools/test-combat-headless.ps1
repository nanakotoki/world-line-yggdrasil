param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2",
    [int]$CombatSteps = 6,
    [switch]$KeepGame,
    [switch]$Verbose
)

# Drives a real combat in a headless Slay the Spire 2 instance through the
# STS2-Agent HTTP API, so the WorldLineYggdrasil combat recorder can be
# exercised without a human player. Requires STS2-Agent v0.9.x installed in
# the game's mods/ folder.
$ErrorActionPreference = "Stop"
$log = "$env:APPDATA\SlayTheSpire2\logs\godot.log"
$appidFile = Join-Path $GameDir "steam_appid.txt"

$env:STS2_ENABLE_DEBUG_ACTIONS = "1"

# Back up the player's real in-progress run saves so a headless test can start
# from a fresh main menu WITHOUT destroying player progress; restored in finally.
$saveBackupDir = "$env:TEMP\wly-save-backup"
function Backup-Saves {
    New-Item -ItemType Directory -Force -Path $saveBackupDir | Out-Null
    $savesRoot = "$env:APPDATA\SlayTheSpire2\steam"
    if (-not (Test-Path $savesRoot)) { return }
    Get-ChildItem -LiteralPath $savesRoot -Recurse -Filter "current_run*.save*" -ErrorAction SilentlyContinue | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $saveBackupDir $_.Name) -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue
    }
}
function Restore-Saves {
    if (-not (Test-Path $saveBackupDir)) { return }
    $savesRoot = "$env:APPDATA\SlayTheSpire2\steam"
    Get-ChildItem -LiteralPath $saveBackupDir -File -ErrorAction SilentlyContinue | ForEach-Object {
        $dest = Get-ChildItem -LiteralPath $savesRoot -Recurse -Filter $_.Name -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($dest) { Copy-Item -LiteralPath $_.FullName -Destination $dest.FullName -Force }
    }
    Write-Host "restored player saves from backup"
}

function Post-Action([hashtable]$body) {
    $payload = $body | ConvertTo-Json -Depth 8
    try {
        return Invoke-RestMethod -Uri "http://127.0.0.1:8080/action" -Method Post `
            -ContentType "application/json; charset=utf-8" -Body $payload -TimeoutSec 30
    }
    catch {
        $resp = $_.Exception.Response
        if ($null -ne $resp) {
            $reader = New-Object System.IO.StreamReader($resp.GetResponseStream())
            $errBody = $reader.ReadToEnd()
            Write-Host "  [api error] $errBody"
            return $null
        }
        throw
    }
}
function Get-State { (Invoke-RestMethod -Uri "http://127.0.0.1:8080/state" -TimeoutSec 10).data }
function Get-Actions { (Invoke-RestMethod -Uri "http://127.0.0.1:8080/actions/available" -TimeoutSec 10).data }
function Wait-Stable {
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Seconds 2
        $s = Get-State
        if ($s.screen -ne "UNKNOWN") { return $s }
    }
    return $s
}

Set-Content -LiteralPath $appidFile -Value "2868840" -NoNewline
try {
    $before = if (Test-Path $log) { (Get-Item $log).Length } else { 0 }
    Backup-Saves
    $proc = Start-Process -FilePath (Join-Path $GameDir "SlayTheSpire2.exe") -ArgumentList "--headless" -PassThru

    $health = $null
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Seconds 3
        try { $health = Invoke-RestMethod -Uri "http://127.0.0.1:8080/health" -TimeoutSec 2; break } catch { }
    }
    if ($null -eq $health) { throw "STS2-Agent /health never became ready" }
    Write-Host "health OK: api_port=$($health.data.api_port)"

    $state = Wait-Stable
    Write-Host "initial screen: $($state.screen)"

    # --- wait for the main menu to settle, clear stale run, start fresh ---
    $menuReady = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        $avail = Get-Actions
        $names = @($avail.actions | ForEach-Object { $_.name })
        if ($names -contains "confirm_modal") {
            Write-Host "> confirm_modal"
            Post-Action @{ action = "confirm_modal" } | Out-Null
            Start-Sleep -Seconds 3
            continue
        }
        if ($names -contains "abandon_run") {
            Write-Host "> abandon_run"
            Post-Action @{ action = "abandon_run" } | Out-Null
            Start-Sleep -Seconds 5
            continue
        }
        if ($names -contains "open_character_select") {
            Write-Host "> open_character_select"
            Post-Action @{ action = "open_character_select" } | Out-Null
            Start-Sleep -Seconds 3
            $menuReady = $true
            break
        }
        Start-Sleep -Seconds 2
    }
    if (-not $menuReady) { throw "character select never became ready" }
    Write-Host "> select_character(0)"
    Post-Action @{ action = "select_character"; option_index = 0 } | Out-Null
    Start-Sleep -Seconds 3
    Write-Host "> embark"
    Post-Action @{ action = "embark" } | Out-Null
    Start-Sleep -Seconds 5

    # --- push through intro/room screens until we reach a combat ---
    $selected = $true
    for ($i = 0; $i -lt 30; $i++) {
        $state = Get-State
        $avail = Get-Actions
        $names = @($avail.actions | ForEach-Object { $_.name })
        if ($Verbose) { Write-Host "  [push] screen=$($state.screen) actions=$($names -join ',')" }
        if ($state.screen -eq "COMBAT") { break }
        if ($state.screen -eq "MAP") {
            Write-Host "> run_console_command room Monster"
            Post-Action @{ action = "run_console_command"; command = "room Monster" } | Out-Null
            Start-Sleep -Seconds 6
            continue
        }
        $picked = $null
        $withIndex = $null
        foreach ($cand in @("proceed", "confirm_modal", "confirm_bundle", "choose_bundle", "choose_capstone_option", "confirm_timeline_overlay", "choose_event_option", "choose_map_node", "choose_rest_option", "collect_rewards_and_proceed", "resolve_rewards", "open_chest", "choose_treasure_relic", "select_deck_card", "dismiss_modal", "choose_reward_card", "claim_reward", "close_shop_inventory")) {
            if ($names -contains $cand) { $picked = $cand; break }
        }
        if ($null -eq $picked) { Write-Host "  [push] no candidate action on $($state.screen); stopping push"; break }
        $body = @{ action = $picked }
        if ($picked -in @("choose_event_option", "choose_map_node", "choose_rest_option", "choose_reward_card", "choose_treasure_relic", "select_deck_card")) { $body.option_index = 0 }
        Write-Host "> $picked"
        Post-Action $body | Out-Null
        Start-Sleep -Seconds 3
    }

    # --- play a short combat ---
    $state = Get-State
    Write-Host "final pre-combat screen: $($state.screen)"
    for ($step = 0; $step -lt $CombatSteps; $step++) {
        # wait until the player has an actionable window (enemy turns resolve here)
        $actionable = $false
        for ($w = 0; $w -lt 40; $w++) {
            $state = Get-State
            $avail = Get-Actions
            $names = @($avail.actions | ForEach-Object { $_.name })
            if ($state.screen -ne "COMBAT") { break }
            if ($names -contains "play_card" -or $names -contains "end_turn") { $actionable = $true; break }
            Start-Sleep -Seconds 2
        }
        $state = Get-State
        if ($state.screen -ne "COMBAT") {
            Write-Host "combat ended on screen $($state.screen)"
            break
        }
        $avail = Get-Actions
        $names = @($avail.actions | ForEach-Object { $_.name })
        Write-Host "  [combat step $step] screen=$($state.screen) actions=$($names -join ',')"

        $played = $false
        if ($null -ne $state.combat) {
            $hand = @($state.combat.hand)
            foreach ($card in $hand) {
                if (-not $card.playable) { continue }
                $body = @{ action = "play_card"; card_index = $card.index }
                if ($card.requires_target -and @($card.valid_target_indices).Count -gt 0) {
                    $body.target_index = $card.valid_target_indices[0]
                }
                Write-Host "> play_card($($card.index)) $($card.card_id) -> target $($body.target_index)"
                Post-Action $body | Out-Null
                $played = $true
                break
            }
        }
        if ($played) { continue }
        if ($names -contains "end_turn") {
            Write-Host "> end_turn"
            Post-Action @{ action = "end_turn" } | Out-Null
            continue
        }
        break
    }

    Start-Sleep -Seconds 5
    if (-not $KeepGame -and -not $proc.HasExited) {
        Stop-Process -Id $proc.Id -Force
        Write-Host "game stopped"
    }

    $content = Get-Content $log -Raw
    $delta = if ($before -lt $content.Length) { $content.Substring([Math]::Max(0, $before)) } else { $content }
    Write-Host "=== WorldLineYggdrasil recorder log ==="
    ($delta -split "`n") | Select-String -Pattern "WorldLineYggdrasil] node|root node|terminal" | ForEach-Object { $_.Line }
}
finally {
    Remove-Item -LiteralPath $appidFile -Force -ErrorAction SilentlyContinue
    Remove-Item Env:\STS2_ENABLE_DEBUG_ACTIONS -ErrorAction SilentlyContinue
    Restore-Saves
    Remove-Item -LiteralPath $saveBackupDir -Recurse -Force -ErrorAction SilentlyContinue
}