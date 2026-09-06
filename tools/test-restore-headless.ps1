param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2",
    [int]$MinStepsBeforeRestore = 6,
    [switch]$KeepGame
)

# Drives a headless combat, then tests WorldLineYggdrasil's deep-snapshot
# restore: lists recorded nodes, restores to the root node, and verifies the
# live player state returned to the root snapshot's HP.
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
        Write-Host "backed up (then cleared) $($_.Name)"
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
            -ContentType "application/json; charset=utf-8" -Body $payload -TimeoutSec 40
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
function Run-Wly([string]$cmdArgs) {
    $r = Post-Action @{ action = "run_console_command"; command = "wly $cmdArgs" }
    return $r
}

Set-Content -LiteralPath $appidFile -Value "2868840" -NoNewline
try {
    Backup-Saves
    $before = if (Test-Path $log) { (Get-Item $log).Length } else { 0 }
    $proc = Start-Process -FilePath (Join-Path $GameDir "SlayTheSpire2.exe") -ArgumentList "--headless" -PassThru

    $health = $null
    for ($i = 0; $i -lt 60; $i++) { Start-Sleep -Seconds 3; try { $health = Invoke-RestMethod -Uri "http://127.0.0.1:8080/health" -TimeoutSec 2; break } catch { } }
    if ($null -eq $health) { throw "health never ready" }
    Write-Host "health OK"

    # menu -> character select -> embark (only when booting to a fresh main menu;
    # if the game resumes an in-progress combat from save, drive it directly)
    $state = Get-State
    if ($state.screen -ne "COMBAT") {
        $menuReady = $false
        for ($a = 0; $a -lt 40; $a++) {
            $names = @((Get-Actions).actions | ForEach-Object { $_.name })
            $state = Get-State
            if ($state.screen -eq "COMBAT") { $menuReady = $true; break }
            if ($names -contains "confirm_modal") { Post-Action @{ action = "confirm_modal" } | Out-Null; Start-Sleep 3; continue }
            if ($names -contains "abandon_run") { Post-Action @{ action = "abandon_run" } | Out-Null; Start-Sleep 5; continue }
            if ($names -contains "open_character_select") { Post-Action @{ action = "open_character_select" } | Out-Null; Start-Sleep 3; $menuReady = $true; break }
            Start-Sleep 2
        }
        if (-not $menuReady) { throw "character select not ready" }
        Post-Action @{ action = "select_character"; option_index = 0 } | Out-Null; Start-Sleep 3
        Post-Action @{ action = "embark" } | Out-Null; Start-Sleep 5

        # push to combat
        for ($i = 0; $i -lt 30; $i++) {
            $state = Get-State
            $names = @((Get-Actions).actions | ForEach-Object { $_.name })
            if ($state.screen -eq "COMBAT") { break }
            if ($state.screen -eq "MAP") { Post-Action @{ action = "run_console_command"; command = "room Monster" } | Out-Null; Start-Sleep 6; continue }
            $picked = $null
            foreach ($cand in @("proceed","confirm_modal","confirm_bundle","choose_bundle","choose_capstone_option","choose_event_option","choose_map_node","collect_rewards_and_proceed","select_deck_card","dismiss_modal")) {
                if ($names -contains $cand) { $picked = $cand; break }
            }
            if ($null -eq $picked) { break }
            $body = @{ action = $picked }
            if ($picked -in @("choose_event_option","choose_map_node","select_deck_card")) { $body.option_index = 0 }
            Post-Action $body | Out-Null; Start-Sleep 3
        }
    }
    else {
        Write-Host "game resumed an in-progress combat from save; driving it directly"
    }

    # play until we've done enough actions to have several nodes
    $actionsTaken = 0
    for ($step = 0; $step -lt 40 -and $actionsTaken -lt $MinStepsBeforeRestore; $step++) {
        for ($w = 0; $w -lt 40; $w++) {
            $state = Get-State
            $names = @((Get-Actions).actions | ForEach-Object { $_.name })
            if ($state.screen -ne "COMBAT") { break }
            if ($names -contains "play_card" -or $names -contains "end_turn") { break }
            Start-Sleep 2
        }
        if ($state.screen -ne "COMBAT") { Write-Host "combat ended early"; break }
        $names = @((Get-Actions).actions | ForEach-Object { $_.name })
        $played = $false
        if ($null -ne $state.combat) {
            foreach ($card in @($state.combat.hand)) {
                if (-not $card.playable) { continue }
                $body = @{ action = "play_card"; card_index = $card.index }
                if ($card.requires_target -and @($card.valid_target_indices).Count -gt 0) { $body.target_index = $card.valid_target_indices[0] }
                Post-Action $body | Out-Null; $actionsTaken++; $played = $true; break
            }
        }
        if (-not $played -and $names -contains "end_turn") {
            Post-Action @{ action = "end_turn" } | Out-Null; $actionsTaken++
        }
    }

    # wait until the player's own turn (guard blocks enemy-turn restores)
    for ($w = 0; $w -lt 40; $w++) {
        $state = Get-State
        $names = @((Get-Actions).actions | ForEach-Object { $_.name })
        if ($state.screen -eq "COMBAT" -and ($names -contains "play_card" -or $names -contains "end_turn")) { break }
        if ($state.screen -ne "COMBAT") { break }
        Start-Sleep 2
    }

    $state = Get-State
    $preHp = $state.combat.player.current_hp
    $preEnergy = $state.combat.player.energy
    Write-Host "pre-restore: hp=$preHp energy=$preEnergy hand=$(@($state.combat.hand).Count)"

    # list nodes via our console command
    $list = Run-Wly "nodes"
    if ($null -eq $list) { throw "wly nodes failed" }
    Write-Host "--- wly nodes ---"
    Write-Host $list.data.message

    # parse root id from header line "combat scope=... root=xxxx"
    $rootMatch = [regex]::Match($list.data.message, 'root=([0-9a-f]+)')
    if (-not $rootMatch.Success) { throw "could not parse root node id" }
    $rootId = $rootMatch.Groups[1].Value
    Write-Host "restoring to root node $rootId (by index 1)"
    $restore = Run-Wly "restore 1"
    if ($null -eq $restore) { throw "wly restore failed (see error above)" }
    Write-Host "restore result: $($restore.data.message)"
    Start-Sleep 3

    $after = Get-State
    $postHp = $after.combat.player.current_hp
    Write-Host "post-restore: hp=$postHp hand=$(@($after.combat.hand).Count) round=$($after.combat.round_number 2>$null)"
    if ($postHp -ge $preHp) { Write-Host "RESTORE OK: hp recovered from $preHp to $postHp" } else { Write-Host "RESTORE POSSIBLY FAILED: hp went from $preHp to $postHp" }

    # check run-level big-node tree
    Start-Sleep 2
    $run = Run-Wly "run"
    if ($null -ne $run) {
        Write-Host "--- wly run (big-node tree) ---"
        Write-Host $run.data.message
    } else {
        Write-Host "wly run failed"
    }

    # test whole-run restore to big node 1 (the event)
    Write-Host "> wly runrestore 1 (reload whole run to event node)"
    $rr = Run-Wly "runrestore 1"
    if ($null -ne $rr) { Write-Host "runrestore result: $($rr.data.message)" } else { Write-Host "runrestore failed" }
    Start-Sleep 8
    $afterReload = Get-State
    Write-Host "after run restore: screen=$($afterReload.screen)"

    Start-Sleep 3
    if (-not $KeepGame -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force; Write-Host "game stopped" }

    $content = Get-Content $log -Raw
    $delta = if ($before -lt $content.Length) { $content.Substring([Math]::Max(0, $before)) } else { $content }
    Write-Host "=== recorder log tail ==="
    ($delta -split "`n") | Select-String -Pattern "WorldLineYggdrasil] node|root node|terminal|restored to|Restore\]" | Select-Object -Last 25 | ForEach-Object { $_.Line }
}
finally {
    Remove-Item -LiteralPath $appidFile -Force -ErrorAction SilentlyContinue
    Remove-Item Env:\STS2_ENABLE_DEBUG_ACTIONS -ErrorAction SilentlyContinue
    Restore-Saves
    Remove-Item -LiteralPath $saveBackupDir -Recurse -Force -ErrorAction SilentlyContinue
}