using Godot;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using WorldLineYggdrasil.Restore;

namespace WorldLineYggdrasil.Restore.Visuals;

/// <summary>
/// The KEY differentiator from the reference impl. Handles three transitions:
///   alive → dead   : remove visual (reference impl already does this).
///   dead  → alive  : recreate visual + restore saved position/scale.
///                   Reference impl uses StartReviveAnim but doesn't restore
///                   position from snapshot, causing the slime/centipede bugs.
///   alive → alive  : update position/scale to saved values (in case the game
///                   re-laid out positions while the creature was "dying").
///
/// Also handles "summoned and undone" — creature was summoned mid-turn, then
/// undo went back before the summon. We detect via "live exists, snap doesn't"
/// and remove the visual + (TODO) the model entry from CombatState.
/// </summary>
internal static class CreatureVisualRefresher
{
    public static void Refresh(CombatSnapshot snap)
    {
        var room = NCombatRoom.Instance;
        if (room == null) { RestoreLogger.Warn("[CreatureVisual] NCombatRoom.Instance null"); return; }

        int aliveOk = 0, revived = 0, killed = 0, missing = 0;

        foreach (var saved in snap.Creatures)
        {
            var live = saved.Ref;
            if (live == null) { missing++; continue; }

            var node = FindNode(room, live);
            bool wasAliveInSnap = !saved.IsDead;
            bool inRemovingList = node != null && IsInRemovingList(room, node);

            string visualsDiag = "";
            if (node != null)
            {
                try
                {
                    var visualsType = ReflectionCache.NCreatureVisualsType;
                    object? visuals = null;
                    if (visualsType != null)
                    {
                        foreach (var n in WalkTree(node))
                            if (visualsType.IsInstanceOfType(n)) { visuals = n; break; }
                    }
                    if (visuals != null)
                    {
                        var phobia = HarmonyLib.AccessTools.Property(visualsType!, "IsUsingPhobiaModeBody")?.GetValue(visuals);
                        var hue = HarmonyLib.AccessTools.Field(visualsType!, "_hue")?.GetValue(visuals);
                        var bodyVis = ((node.Body as Godot.CanvasItem)?.Visible);
                        visualsDiag = $" phobia={phobia} hue={hue} bodyVis={bodyVis}";
                    }
                }
                catch { }
            }
            RestoreLogger.Info($"[CreatureVisual] id={saved.CombatId} snap.IsDead={saved.IsDead} " +
                $"live.IsDead={live.IsDead} hp={live.CurrentHp}/{live.MaxHp} " +
                $"node={(node != null ? "exists" : "null")} inRemoving={inRemovingList}{visualsDiag}");

            // Revive-power creatures (IllusionPower etc.): vanilla AnimDie runs
            // unmodified for them and the power's own state machine drives the
            // visual lifecycle. Skip body / spine / hue / overlay manipulation
            // (fights with the power's visual). DO still run RefreshIntents
            // and ResetTargetingState below — those are state resets on the
            // NCreature itself, not body manipulation, and skipping them broke
            // hover/click targeting after revive (reported 2026-04-30: "after
            // revive can't target with attacks, undo issue").
            //
            // Narrower gate `isLiveReviveAnim` — only skip body manipulation
            // when there's a LIVE revive anim to fight with (IllusionPower
            // etc.). DieForYou (Osty) is revive-like for AnimDie/StartDeathAnim
            // delegation purposes but revives instantly (no live anim), so
            // undo can safely restore Osty's body state. Reported 2026-05-08.
            bool isReviveLikeCreature = node != null
                && Patches.AnimDiePatch.FindReviveLikePower(node) != null;
            bool isLiveReviveAnim = node != null
                && Patches.AnimDiePatch.FindLiveReviveAnimPower(node) != null;
            if (isReviveLikeCreature)
            {
                RestoreLogger.Warn($"[CreatureVisual] id={saved.CombatId} has revive-like power — skipping body manipulation, keeping targeting reset");
                if (wasAliveInSnap) revived++; // counted for log accounting
            }

            // Vanilla AnimDie may have already nulled out NCreatureVisuals._body
            // / SpineBody on the zombie even though the NCreature shell is still
            // discoverable. Without intervention every downstream step throws
            // "Cannot access a disposed object". TryRestoreBodyFromSavedRef
            // recovers from the VFX-reparent case; if both the live body and
            // the saved ref are freed the body is genuinely unrecoverable and
            // we skip the tail block to avoid the cascade of disposed-object
            // exceptions.
            bool zombieDegraded = node != null && Restore.SnapshotRestorer.IsZombieDegraded(node);
            if (zombieDegraded && node != null)
            {
                RestoreLogger.Warn($"[CreatureVisual] zombie degraded id={saved.CombatId} inRemoving={inRemovingList} — attempting body recovery via saved BodyRef");
                try { Restore.SnapshotRestorer.TryRestoreBodyFromSavedRef(node, saved); }
                catch (Exception ex) { RestoreLogger.Warn($"[CreatureVisual] TryRestoreBodyFromSavedRef failed id={saved.CombatId}: {ex.Message}"); }
                // Re-probe — recovery may have re-attached the body and lifted
                // the degraded flag. If still degraded, all subsequent manipulation
                // is unsafe so we skip the tail block (loop `continue` below).
                zombieDegraded = Restore.SnapshotRestorer.IsZombieDegraded(node);
                RestoreLogger.Warn($"[CreatureVisual] post-recovery id={saved.CombatId} stillDegraded={zombieDegraded}");
            }

            if (wasAliveInSnap)
            {
                if (node == null)
                {
                    // Visual was QueueFree'd on death — recreate.
                    try { room.AddCreature(live); revived++; }
                    catch (Exception ex) { RestoreLogger.Warn($"[CreatureVisual] AddCreature failed id={saved.CombatId}: {ex.Message}"); }
                    node = FindNode(room, live);
                    if (node != null) try { node.StartReviveAnim(); } catch { }
                }
                else if (zombieDegraded)
                {
                    // Body recovery couldn't reattach. Falling back to in-place
                    // StartReviveAnim — on a stripped shell it silently no-ops,
                    // but at least we don't free anything else.
                    revived++;
                }
                else if (inRemovingList)
                {
                    // Mid-death animation, body still intact — pull back via revive.
                    try { node.StartReviveAnim(); revived++; } catch { }
                }
                else { aliveOk++; }
            }

            // If body recovery failed, the manipulation block below is unsafe.
            // Skip the rest of this loop iteration.
            if (zombieDegraded) { continue; }

            // Body / spine / hue / overlay restoration applies regardless of
            // dead-or-alive snapshot. The captured VisualBodyModulate /
            // SpineAnimNameTrack0 / Hue carry whatever pose the creature was
            // in at capture time — for an alive capture this is the live
            // pose; for a corpse capture (e.g. 만각지네 segment waiting to
            // revive) it's the die-loop pose. Without applying these to the
            // dead-snap branch, undoing back to a corpse state on a creature
            // that was revived since leaves it visually alive.
            //
            // Skipped only for LIVE-revive-anim powers (IllusionPower etc.) —
            // vanilla's running tween/state-machine would clobber our writes.
            // DieForYou is revive-like for AnimDie purposes but revives
            // instantly with no live anim → safe to restore here.
            if (!isLiveReviveAnim && node != null && saved.HadVisualNode)
            {
                if (wasAliveInSnap)
                {
                    // Order matters: stop AnimationPlayers FIRST, then write
                    // pose/transform. AnimationPlayer can drive the spine track
                    // forward; if we set spine first then stop AP, AP may have
                    // re-issued an attack on the next tick before stopping.
                    // Skip for dead-in-snap — TryCancelHurtAnim resets body
                    // position/rotation/modulate to defaults, which would
                    // overwrite the corpse pose we're about to restore.
                    TryCancelHurtAnim(node);
                }

                try { node.GlobalPosition = saved.VisualPosition; } catch { }
                try
                {
                    var body = node.Body;
                    if (body != null)
                    {
                        body.Scale = saved.VisualBodyScale;
                        // body.Position / Rotation: AnimDie or hurt anim
                        // can leave these displaced. Restore from snapshot.
                        body.Position = saved.VisualBodyPosition;
                        body.Rotation = saved.VisualBodyRotation;
                        // Force-show only when restoring an alive snapshot;
                        // for corpse-snapshot, AnimDiePatch may have hidden
                        // the body and we want to leave that alone (the
                        // captured Modulate alpha already handles the
                        // visual fade).
                        if (wasAliveInSnap) body.Visible = true;
                        if (body is Godot.CanvasItem bodyCi)
                            bodyCi.Modulate = saved.VisualBodyModulate;
                    }
                }
                catch { }
                // Also restore NCreatureVisuals visibility (also hidden by
                // AnimDiePatch). Without this, body's Visible=true above
                // doesn't help since the parent still skips rendering.
                if (wasAliveInSnap)
                {
                    try
                    {
                        if (node.Visuals is Godot.Node2D visualsN2D)
                            visualsN2D.Visible = true;
                    }
                    catch { }
                }

                    // Reset death-tint shader state. NCreatureVisuals._hue ramps
                    // toward 1.0 during death anim; reused-shell revive leaves it
                    // stuck → body invisible even with transforms correct.
                    try
                    {
                        var visuals = node.Visuals;
                        if (visuals != null && saved.Hue.HasValue
                            && ReflectionCache.NCVHueField is var hf && hf != null)
                        {
                            float prev = (hf.GetValue(visuals) as float?) ?? -1f;
                            hf.SetValue(visuals, saved.Hue.Value);
                            RestoreLogger.Info($"[CreatureVisual] hue restore id={saved.CombatId} {prev}→{saved.Hue.Value}");
                        }
                        if (visuals != null && saved.LiquidOverlayTimer.HasValue
                            && ReflectionCache.NCVLiquidOverlayTimerField is var tf && tf != null)
                        {
                            tf.SetValue(visuals, saved.LiquidOverlayTimer.Value);
                        }

                        // Force-clear the liquid overlay shader. The async
                        // ApplyLiquidOverlayInternal loop only cleans up when its
                        // own timer hits 0; if our undo lands while a brand-new
                        // overlay was applied (and the loop is still running) the
                        // body's normal material is the goopy potion shader and
                        // stays that way until the loop happens to exit. Slimy
                        // creatures and post-power-apply visuals end up with the
                        // overlay stuck on. Reset directly: 0 the timer, swap body
                        // material back to the saved-at-snapshot baseline, null
                        // the overlay/saved fields.
                        if (visuals != null)
                        {
                            try
                            {
                                ReflectionCache.NCVLiquidOverlayTimerField?.SetValue(visuals, 0.0);

                                var spineBodyProp = HarmonyLib.AccessTools.Property(visuals.GetType(), "SpineBody");
                                var spineBody = spineBodyProp?.GetValue(visuals);
                                if (spineBody != null && saved.BodyNormalMaterial != null
                                    && ReflectionCache.MegaSpriteSetNormalMaterialMethod != null)
                                {
                                    ReflectionCache.MegaSpriteSetNormalMaterialMethod
                                        .Invoke(spineBody, new object[] { saved.BodyNormalMaterial });
                                    RestoreLogger.Info($"[CreatureVisual] overlay cleared id={saved.CombatId} wasActive={saved.LiquidOverlayWasActive}");
                                }

                                ReflectionCache.NCVCurrentLiquidOverlayMaterialField?.SetValue(visuals, null);
                                // Mirror _savedNormalMaterial to the same value we
                                // just wrote so a still-running loop, if any, will
                                // try to restore-back to the same material instead
                                // of nulling it out.
                                ReflectionCache.NCVSavedNormalMaterialField?.SetValue(visuals, saved.BodyNormalMaterial);
                            }
                            catch (Exception ex) { RestoreLogger.Warn($"[CreatureVisual] overlay clear failed: {ex.Message}"); }
                        }
                    }
                    catch (Exception ex) { RestoreLogger.Warn($"[CreatureVisual] hue restore failed: {ex.Message}"); }

                    // Resolve desired pose: snapshot's stored value first; if
                    // null (capture had no stable observation), fall back to
                    // the live cache (which may have been updated by a later
                    // observation than this snapshot).
                    string? desired = saved.SpineAnimNameTrack0;
                    if (string.IsNullOrEmpty(desired) && live != null
                        && CombatSnapshot.IdleAnimCache.TryGetValue(live, out var cached))
                        desired = cached;
                if (!string.IsNullOrEmpty(desired))
                    TryRestoreSpineAnim(node, desired!);
            }

            // RefreshIntents applies to both alive and dead-in-snap (corpse).
            // For corpses (e.g. 만각지네 segment waiting to revive), the intent
            // shows the revive countdown.
            try { _ = node?.RefreshIntents(); } catch { }

            // For alive-in-snap creatures with a live node (the broad case —
            // includes everything that doesn't go through ReviveCreature):
            // restore targeting state. Common scenarios:
            //   1. Mid-AnimDie undo (within 1.5s of death) — node still in
            //      tree, IsDead momentarily true, snap restores it alive.
            //   2. corpse-with-revive-intent monster (만각지네) where snap
            //      had it alive — IsDead never went true.
            //   3. Already-alive but undo to earlier alive state.
            // In all cases, if mouse was hovering at death moment OR if
            // animation flipped Hitbox state, the next mouse hover skips
            // NTargetManager.OnNodeHovered because IsFocused is stuck or
            // MouseFilter is wrong.
            if (!saved.IsDead && node != null)
            {
                // Revive-power creatures (IllusionPower etc.): vanilla's
                // AnimDie may have flipped Hitbox.MouseFilter to Ignore /
                // FocusMode to None / Visible to false during the death anim
                // and the power's revive doesn't fully restore them. Need the
                // full hitbox restore + ToggleIsInteractable, not just
                // IsFocused — otherwise after revive + undo the player can't
                // target the creature with a single-target attack.
                // Reported 2026-04-30: "after revive can't target with attacks
                // — undo issue".
                ResetTargetingState(node, saved.CombatId, fullRestore: isReviveLikeCreature);
            }
        }

        RestoreLogger.Info($"[CreatureVisual] aliveOk={aliveOk} revived={revived} killed={killed} missing={missing}");

        // Final pass: tell each creature's NCreatureStateDisplay to repaint
        // HP/block/nameplate/healthbar UI from the now-restored model values.
        // Model-side block can drop to 0 (turn boundary or undo of a block-
        // gaining card) without firing the BlockChanged delegate during the
        // restore step, so the BlockOutline 9-slice stays drawn over the
        // sprite. Same for HP foreground vs the restored CurrentHp.
        int refreshed = 0;
        foreach (var saved in snap.Creatures)
        {
            var creature = saved.Ref;
            if (creature == null) continue;
            var node = room.GetCreatureNode(creature);
            if (node == null) continue;

            // Revive-power creatures (IllusionPower etc.): vanilla drives its own
            // AnimateOut/AnimateIn lifecycle. Two failure modes seen during the
            // 2026-04-30 testing pass:
            //   - Force-write Visible/Modulate based on snap.IsDead → fought
            //     vanilla mid-animation, made HP bar inconsistent during normal
            //     gameplay (sometimes hidden after revive, sometimes shown).
            //   - Skip force entirely (values-only RefreshValues) → undo to
            //     alive snapshot left HP bar hidden (vanilla's AnimateOut state
            //     persisted across the undo).
            // Compromise: force VISIBLE only when snap.IsDead==false (caller
            // intent: "should be alive now"). Skip the force-hide branch — let
            // vanilla's AnimateOut keep playing if it's mid-animation, or stay
            // hidden if already done. This restores HP bar after undo without
            // ramming into vanilla's mid-death animation.
            //
            // Narrower gate `isLiveReviveAnim` — only skip force-hide when
            // there's a LIVE revive anim to fight with (IllusionPower etc.).
            // DieForYou (Osty) revives instantly so undo can safely force-hide
            // its StateDisplay when restoring a dead-in-snap state.
            bool isReviveLike = Patches.AnimDiePatch.FindReviveLikePower(node) != null;
            bool isLiveReviveAnim = Patches.AnimDiePatch.FindLiveReviveAnimPower(node) != null;

            foreach (var n in WalkTree(node))
            {
                if (n is not NCreatureStateDisplay sd) continue;
                try
                {
                    // AnimateOut sequence (vanilla NCreatureStateDisplay.AnimateOut):
                    //   1. Tween Modulate.A → 0 (Sine, ~0.5s)
                    //   2. Tween Position → Position - _healthBarAnimOffset (Quad, ~0.25s)
                    //   3. TweenCallback: Visible = false
                    // If undo fires while any of these are still running, our
                    // force-show below gets clobbered: the modulate tween keeps
                    // lerping toward 0, the callback re-fires Visible=false a
                    // tick later, or the position tween leaves the bar offset
                    // off-screen. Reported 2026-05-02 (seed Y50J2MGVWX3) —
                    // TestSubject HP bar invisible after undo across phase-1
                    // death. Save/load fixed it (fresh tree, no live tween).
                    // Kill _showHideTween + _hoverTween BEFORE writing visibility
                    // to settle the race deterministically.
                    KillStateDisplayTween(sd, ReflectionCache.NCreatureStateDisplayShowHideTweenField);
                    KillStateDisplayTween(sd, ReflectionCache.NCreatureStateDisplayHoverTweenField);

                    // NCreatureStateDisplay.AnimateOut runs on death, leaving the
                    // node Visible=false and Modulate.A=0. The HP bar (NHealthBar)
                    // is its child — when restoring an ALIVE snapshot, we need
                    // to undo that fade-out so the bar reappears.
                    // For a DEAD-in-snapshot creature (corpse), the game's
                    // AnimateOut state is the correct visual: hidden bar + no
                    // "DEAD" text. If we force-show, RefreshText sees CurrentHp=0
                    // and writes "DEAD" into the HP label; force-hidden keeps
                    // the corpse looking like a corpse.
                    var mod = sd.Modulate;
                    if (!saved.IsDead)
                    {
                        sd.Visible = true;
                        mod.A = 1f;
                        sd.Modulate = mod;
                        // Reset Position to the captured _originalPosition so the
                        // bar isn't left offset by AnimateOut's _healthBarAnimOffset
                        // shift. Snap, don't tween — undo wants instant restore.
                        if (ReflectionCache.NCreatureStateDisplayOriginalPositionField?.GetValue(sd)
                            is Godot.Vector2 origPos)
                        {
                            sd.Position = origPos;
                        }
                    }
                    else if (!isLiveReviveAnim)
                    {
                        sd.Visible = false;
                        mod.A = 0f;
                        sd.Modulate = mod;
                    }
                    // (Live-revive-anim + dead-in-snap: skip force-hide — vanilla
                    //  owns the AnimateOut tween. Force-writing here would snap
                    //  to A=0 mid-tween and create the inconsistent gameplay
                    //  HP-bar behavior the user reported.)

                    ReflectionCache.NCreatureStateDisplayRefreshValuesMethod?.Invoke(sd, null);
                    refreshed++;
                }
                catch (Exception ex)
                { RestoreLogger.Warn($"[CreatureVisual] StateDisplay.RefreshValues: {ex.Message}"); }
            }
        }
        if (refreshed > 0)
            RestoreLogger.Info($"[CreatureVisual] refreshed {refreshed} StateDisplay(s)");
    }

    /// <summary>
    /// Force-clear stuck IsFocused on alive-in-snap creatures. If the mouse
    /// was hovering a creature at the moment of death (very common — player
    /// just attacked, mouse still over the target), MouseEntered → OnFocus
    /// set IsFocused = true. AnimDiePatch detaches NCreature before the
    /// mouse leaves the hitbox area, so MouseExited never fires → IsFocused
    /// stays true. Next MouseEntered after revive sees IsFocused == true and
    /// returns immediately, skipping NTargetManager.OnNodeHovered.
    /// Single-target needs the hover signal; AOE bypasses hover entirely
    /// (which is why the user reported "AOE works, single-target doesn't").
    ///
    /// Minimal-touch fix: reset IsFocused only. Earlier versions also set
    /// MouseFilter/FocusMode and called UpdateBounds + OnUnfocus — those
    /// turned out to be unnecessary (hitbox was already correct in probe.log
    /// diagnostics) and the OnUnfocus call mutated NTargetManager.HoveredNode
    /// global state, causing stuck nameplates on other enemies.
    ///
    /// <paramref name="fullRestore"/> = true: also restore Hitbox state +
    /// ToggleIsInteractable. Needed for revive-power creatures (IllusionPower
    /// etc.) where vanilla's death+revive flow leaves the hitbox in a non-
    /// targetable state. Mirrors DeathAnimDelayPatch.CollectAndReset's
    /// alive-creature path (the same fix applied for TestSubject phase-2).
    /// </summary>
    private static void ResetTargetingState(NCreature node, uint combatId, bool fullRestore = false)
    {
        try
        {
            bool resetSomething = false;
            foreach (var fieldName in new[] {
                "<IsFocused>k__BackingField", "_isFocused", "isFocused" })
            {
                var f = HarmonyLib.AccessTools.Field(typeof(NCreature), fieldName);
                if (f != null)
                {
                    f.SetValue(node, false);
                    resetSomething = true;
                }
            }
            if (!resetSomething)
                RestoreLogger.Warn("[Targeting] could not find IsFocused backing field on NCreature");

            if (fullRestore)
            {
                // Hitbox state — vanilla AnimDie may have flipped MouseFilter to
                // Ignore / FocusMode to None during the death anim and not
                // restored them after revive. Without these the next hover
                // skips NTargetManager.OnNodeHovered.
                try
                {
                    if (node.Hitbox != null)
                    {
                        var prevMf = node.Hitbox.MouseFilter;
                        var prevFm = node.Hitbox.FocusMode;
                        var prevVis = node.Hitbox.Visible;
                        node.Hitbox.MouseFilter = Godot.Control.MouseFilterEnum.Stop;
                        node.Hitbox.FocusMode = Godot.Control.FocusModeEnum.All;
                        node.Hitbox.Visible = true;
                        if (prevMf != Godot.Control.MouseFilterEnum.Stop
                            || prevFm != Godot.Control.FocusModeEnum.All
                            || !prevVis)
                        {
                            RestoreLogger.Warn($"[Targeting] hitbox restore id={combatId} mf {prevMf}->Stop fm {prevFm}->All vis {prevVis}->True");
                        }
                    }
                }
                catch (Exception ex)
                { RestoreLogger.Warn($"[Targeting] hitbox restore failed id={combatId}: {ex.Message}"); }

                // Belt-and-braces — same call DeathAnimDelayPatch uses for the
                // post-phase-transition alive sweep.
                try { node.ToggleIsInteractable(node.Entity?.Monster?.IsHealthBarVisible ?? true); }
                catch (Exception ex)
                { RestoreLogger.Warn($"[Targeting] ToggleIsInteractable failed id={combatId}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { RestoreLogger.Warn($"[Targeting] reset failed id={combatId}: {ex.Message}"); }
    }

    private static NCreature? FindNode(NCombatRoom room, Creature live)
    {
        var node = room.GetCreatureNode(live);
        if (node != null) return node;

        // Fallback to _removingCreatureNodes (creature mid-death animation).
        if (ReflectionCache.NcrRemovingNodesField?.GetValue(room)
            is System.Collections.IEnumerable removing)
        {
            foreach (var item in removing)
            {
                if (item is NCreature nc
                    && ReflectionCache.NCreatureEntityProp?.GetValue(nc) is Creature ent
                    && ReferenceEquals(ent, live))
                    return nc;
            }
        }
        return null;
    }

    /// <summary>
    /// NCreatureVisuals has IsPlayingHurtAnimation() but no public Skip/Cancel
    /// method — so we can't ask the game to stop an in-flight hit animation.
    /// Instead, we stop every AnimationPlayer/Tween in the subtree and force the
    /// Body transform/modulate back to defaults. The hurt anim's residue (color
    /// flash, recoil offset, rotation, frame seek) lives on those nodes.
    /// </summary>
    private static readonly string[] TransientAnimSubstrings =
        { "attack", "cast", "hurt", "hit", "damage", "die", "death", "spawn" };
    // NOTE: stun/knock/freeze/sleep/daze deliberately omitted. Those are
    // *valid* spine poses when the model has the matching power active —
    // forcing idle_loop in those cases corrupted screenshots where the user
    // captured during a stunned-loop state (Corpse Slug stun pose). The
    // snapshot's SpineAnimNameTrack0 is the ground truth: if it captured
    // stunned, restore stunned. LooksStableLoop still rejects these from
    // IdleAnimCache so the *fallback* path doesn't poison true-idle
    // restores when the snapshot didn't capture an explicit anim.

    /// <summary>
    /// Only intervene when track 0 is currently a *transient* anim (attack /
    /// hurt / cast / etc.). Replace with a stable idle loop so the pose isn't
    /// stuck. Stable loops (idle_loop / block_loop / low_hp_loop / ...) reflect
    /// the live model state and are managed by the game's own state machine —
    /// touching them would override the contextual pose with a generic one.
    /// </summary>
    private static void TryRestoreSpineAnim(NCreature node, string animHint)
    {
        try
        {
            var visualsType = ReflectionCache.NCreatureVisualsType;
            var setAnim = ReflectionCache.SpineSetAnimationMethod;
            var spineProp = ReflectionCache.NCVSpineAnimationProp;
            if (visualsType == null || setAnim == null || spineProp == null) return;

            object? visuals = null;
            foreach (var n in WalkTree(node))
            {
                if (visualsType.IsInstanceOfType(n)) { visuals = n; break; }
            }
            if (visuals == null) return;

            var spine = spineProp.GetValue(visuals);
            if (spine == null) return;

            string? currentName = null;
            try
            {
                var track = ReflectionCache.SpineGetCurrentTrackMethod?.Invoke(spine, new object[] { 0 });
                if (track != null)
                {
                    var anim = ReflectionCache.TrackGetAnimationMethod?.Invoke(track, null);
                    if (anim != null)
                    {
                        var getName = HarmonyLib.AccessTools.Method(anim.GetType(), "GetName");
                        if (getName?.Invoke(anim, null) is string s) currentName = s;
                    }
                }
            }
            catch { }

            // Force track 0 to the snapshot's recorded pose. animHint is the
            // contextual loop the creature was in at snapshot time
            // (idle_loop / block_loop / low_hp_loop / ...). Capture side already
            // filters non-loops to use cache fallback, so an animHint shaped
            // like a loop should be a faithful pose.
            //
            // If hint isn't loop-shaped (capture had no stable observation,
            // cache was empty), we still need to handle stuck transients — fall
            // back to "idle_loop" but only if current is itself transient.
            // Spine targeting policy (rewritten again 2026-04-27 — final):
            // The snapshot's hint IS the truth. Undo means "go back to before",
            // and the captured spine name reflects the visual at that prior
            // moment. If model state at capture had no stun power, spine was
            // idle — restore that. If model had stun, spine was devour_loop —
            // restore that. We earlier mistakenly trusted live; that froze
            // post-effect visuals (e.g. ravenous-triggered devour_loop) on
            // a model that had been rolled back to a non-stunned state, so
            // the slug looked stunned with no actual stun power.
            //
            //  - hint loop-shaped → set track 0 = hint.
            //  - hint missing AND live transient (attack/hurt/cast/die/spawn)
            //    → fall back to idle_loop so the creature doesn't freeze
            //    mid-action.
            //  - else skip.
            string? target = null;
            if (!string.IsNullOrEmpty(animHint)
                && (animHint!.IndexOf("loop", StringComparison.OrdinalIgnoreCase) >= 0
                    || animHint.IndexOf("idle", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                target = animHint;
            }
            else if (currentName != null && IsTransient(currentName))
            {
                target = "idle_loop";
            }

            if (target == null)
            {
                RestoreLogger.Info($"[CreatureVisual] spine skip: hint='{animHint ?? "<null>"}' current='{currentName ?? "<null>"}'");
                return;
            }
            if (string.Equals(currentName, target, StringComparison.Ordinal))
            {
                RestoreLogger.Info($"[CreatureVisual] spine no-op: hint='{animHint}' current='{currentName}'");
                return;
            }

            // BEFORE setting the new animation, reset bones to setup pose.
            // Without this, spine interpolates from current bone positions
            // (which after 'die' are corpse pose) toward idle_loop's start —
            // visually the creature stays slumped/dead-shaped. Setup pose snap
            // gives a clean baseline for idle_loop to play from.
            try
            {
                var spineBodyProp = HarmonyLib.AccessTools.Property(visualsType, "SpineBody");
                var megaSprite = spineBodyProp?.GetValue(visuals);
                var skel = ReflectionCache.MegaSpriteGetSkeletonMethod?.Invoke(megaSprite, null);
                if (skel != null)
                {
                    ReflectionCache.SkeletonSetSlotsToSetupPoseMethod?.Invoke(skel, null);
                    var setBonesToSetupPose = HarmonyLib.AccessTools.Method(skel.GetType(), "SetBonesToSetupPose");
                    setBonesToSetupPose?.Invoke(skel, null);
                    var setToSetupPose = HarmonyLib.AccessTools.Method(skel.GetType(), "SetToSetupPose");
                    setToSetupPose?.Invoke(skel, null);
                }
            }
            catch (Exception ex) { RestoreLogger.Warn($"[CreatureVisual] setup-pose reset failed: {ex.Message}"); }

            var entry = setAnim.Invoke(spine, new object[] { target, true, 0 });
            if (entry != null)
            {
                try
                {
                    var setMix = HarmonyLib.AccessTools.Method(entry.GetType(), "SetMixDuration");
                    setMix?.Invoke(entry, new object[] { 0f });
                }
                catch { }
                // SetTrackTime(0) removed — was freezing the spine animation
                // mid-progression for stunned_loop. Letting the new SetAnimation
                // call start at its natural 0 keeps spine's process advancing.
                try
                {
                    var setTimeScale = HarmonyLib.AccessTools.Method(entry.GetType(), "SetTimeScale");
                    setTimeScale?.Invoke(entry, new object[] { 1f });
                }
                catch { }
            }
            // Force track 0 timescale on the SpineAnimationAccess level too.
            // The death sequence can leave the underlying spine sprite at
            // timeScale=0 (paused), so even after setAnim the loop never
            // advances frames. Restoring 1 unblocks playback.
            try
            {
                var setSpineTimeScale = HarmonyLib.AccessTools.Method(spine.GetType(), "SetTimeScale");
                setSpineTimeScale?.Invoke(spine, new object[] { 1f });
            }
            catch { }
            try
            {
                var spineBodyProp = HarmonyLib.AccessTools.Property(visualsType, "SpineBody");
                var megaSprite = spineBodyProp?.GetValue(visuals);
                if (megaSprite != null)
                {
                    var setMsTimeScale = HarmonyLib.AccessTools.Method(megaSprite.GetType(), "SetTimeScale");
                    setMsTimeScale?.Invoke(megaSprite, new object[] { 1f });
                }
            }
            catch { }

            // Track 1-3 overlay handling. Previously we force-set these to
            // `target` (= track-0 anim) which inadvertently overwrote stunned
            // overlay layers (the small star/effect overlay layered with
            // stunned_loop). Only normalize when target is a plain idle/block
            // loop (true-idle restore) — for incapacitated targets, leave
            // the captured overlay tracks alone.
            bool targetIsIncapacitated =
                target.IndexOf("stun", StringComparison.OrdinalIgnoreCase) >= 0
                || target.IndexOf("knock", StringComparison.OrdinalIgnoreCase) >= 0
                || target.IndexOf("freeze", StringComparison.OrdinalIgnoreCase) >= 0
                || target.IndexOf("sleep", StringComparison.OrdinalIgnoreCase) >= 0
                || target.IndexOf("daze", StringComparison.OrdinalIgnoreCase) >= 0;

            int overlaysCleared = 0;
            if (!targetIsIncapacitated)
            {
                for (int ti = 1; ti <= 3; ti++)
                {
                    try
                    {
                        var t = ReflectionCache.SpineGetCurrentTrackMethod?.Invoke(spine, new object[] { ti });
                        if (t == null) continue;
                        var a = ReflectionCache.TrackGetAnimationMethod?.Invoke(t, null);
                        if (a == null) continue;
                        var n = HarmonyLib.AccessTools.Method(a.GetType(), "GetName")?.Invoke(a, null) as string;
                        if (string.IsNullOrEmpty(n) || string.Equals(n, target, StringComparison.Ordinal)) continue;
                        setAnim.Invoke(spine, new object[] { target, true, ti });
                        overlaysCleared++;
                    }
                    catch { }
                }
            }

            RestoreLogger.Info($"[CreatureVisual] spine refresh: track0='{currentName}'→'{target}' overlays={overlaysCleared} incapacitated={targetIsIncapacitated}");
        }
        catch (Exception ex) { RestoreLogger.Warn($"[CreatureVisual] spine restore failed: {ex.Message}"); }
    }

    private static bool IsTransient(string name)
    {
        foreach (var s in TransientAnimSubstrings)
            if (name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        return false;
    }

    private static void TryCancelHurtAnim(NCreature node)
    {
        bool wasPlaying = false;
        try
        {
            var isPlayingMethod = HarmonyLib.AccessTools.Method(
                ReflectionCache.HurtAnimType ?? typeof(NCreature), "IsPlayingHurtAnimation");
            if (isPlayingMethod != null)
            {
                foreach (var n in WalkTree(node))
                {
                    var t = ReflectionCache.HurtAnimType;
                    if (t != null && !t.IsInstanceOfType(n)) continue;
                    try
                    {
                        if (isPlayingMethod.Invoke(n, null) is true) { wasPlaying = true; break; }
                    }
                    catch { }
                }
            }
        }
        catch { }

        int stoppedAnims = 0;
        foreach (var n in WalkTree(node))
        {
            if (n is Godot.AnimationPlayer ap)
            {
                try
                {
                    if (ap.IsPlaying())
                    {
                        ap.Stop(keepState: false);
                        stoppedAnims++;
                    }
                }
                catch { }
            }
        }

        // Force-clear residual Body transform/modulate.
        try
        {
            var body = node.Body;
            if (body != null)
            {
                body.Position = Godot.Vector2.Zero;
                body.RotationDegrees = 0f;
                body.Modulate = Godot.Colors.White;
                body.SelfModulate = Godot.Colors.White;
            }
        }
        catch (Exception ex) { RestoreLogger.Warn($"[CreatureVisual] body reset failed: {ex.Message}"); }

        RestoreLogger.Info($"[CreatureVisual] hurt-anim cleanup: wasPlaying={wasPlaying} stoppedAnims={stoppedAnims}");
    }

    /// <summary>
    /// Read a Tween out of an `NCreatureStateDisplay` field via reflection
    /// and Kill() it if still valid. Used to abort a mid-flight AnimateOut
    /// (or AnimateIn) before our snapshot restore force-writes Visible /
    /// Modulate / Position — without the kill, the tween keeps lerping and
    /// the trailing TweenCallback re-hides the bar a tick later.
    /// </summary>
    private static void KillStateDisplayTween(NCreatureStateDisplay sd, System.Reflection.FieldInfo? field)
    {
        if (field == null) return;
        try
        {
            if (field.GetValue(sd) is Godot.Tween tw
                && Godot.GodotObject.IsInstanceValid(tw)
                && tw.IsValid())
            {
                tw.Kill();
            }
        }
        catch { /* best-effort — tween may already be disposed */ }
    }

    private static IEnumerable<Godot.Node> WalkTree(Godot.Node root)
    {
        var stack = new Stack<Godot.Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            foreach (var c in n.GetChildren()) stack.Push(c);
        }
    }

    private static bool IsInRemovingList(NCombatRoom room, NCreature node)
    {
        if (ReflectionCache.NcrRemovingNodesField?.GetValue(room)
            is not System.Collections.IEnumerable removing) return false;
        foreach (var item in removing)
            if (ReferenceEquals(item, node)) return true;
        return false;
    }
}
