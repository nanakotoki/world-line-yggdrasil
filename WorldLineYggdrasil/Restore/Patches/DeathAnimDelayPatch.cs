using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Combat;
using WorldLineYggdrasil.Restore;

namespace WorldLineYggdrasil.Restore.Patches;

/// <summary>
/// Defers <c>NCreature.StartDeathAnim</c> by a short window so a fast undo can
/// abort the death animation before it ever runs. Without this, the async
/// <c>AnimDie</c> task starts immediately on kill and frees the creature's
/// body subtree synchronously between awaits — by the time undo arrives and
/// we cancel the cancellation token, the body is already gone and the in-place
/// revive path produces a half-dead shell.
///
/// Strategy:
///   1. Prefix on <c>StartDeathAnim</c> intercepts the call.
///   2. Schedules a <see cref="Godot.Timer"/> child on the NCreature with a
///      short wait. When the timer times out, we re-invoke <c>StartDeathAnim</c>
///      with a bypass flag set so our prefix lets it through.
///   3. If <see cref="AbortAllPending"/> is called within the window (e.g. from
///      <c>SnapshotRestorer.Restore</c>'s entry), the timer is stopped and
///      freed — the death anim never runs and the creature stays alive in tree.
///
/// Trade-off: the player sees a brief frozen pose at the moment of kill before
/// the death anim begins. <see cref="DeferSeconds"/> is tuned to be barely
/// noticeable. Slower undos (after the timer has fired) still need the
/// post-revive degraded-detection + AddCreature fallback.
/// </summary>
[HarmonyPatch(typeof(NCreature), "StartDeathAnim")]
public static class DeathAnimDelayPatch
{
    public const float DeferSeconds = 0.2f;

    private static readonly Dictionary<ulong, (Godot.Timer timer, NCreature creature, bool arg)> _pending
        = new();
    private static bool _bypass;

    [HarmonyPrefix]
    public static bool Prefix(NCreature __instance, bool __0)
    {
        // Re-entrant call from the deferred timer: let the original StartDeathAnim run.
        if (_bypass) return true;

        try
        {
            if (!Godot.GodotObject.IsInstanceValid(__instance)) return true;

            // Monsters whose vanilla StartDeathAnim/AnimDie runs phase-transition
            // logic (TestSubject phase-1 → phase-2) need vanilla's synchronous
            // call timing — deferring 0.2s desyncs the transition with the
            // monster state machine and the regular spine 'die' track ends up
            // playing before/instead of the proper transition anim. These
            // monsters are already on AnimDiePatch's skiplist (we don't replace
            // their AnimDie), and undo across their death isn't supported, so
            // the defer's whole reason-for-existing (fast-undo abort) doesn't
            // apply — pass through immediately.
            if (TryGetMonsterTypeName(__instance) is { } typeName
                && AnimDiePatch.SkipReplacementMonsterTypes.Contains(typeName))
            {
                RestoreLogger.Warn($"[DeathDefer] monster={typeName} instId={__instance.GetInstanceId()} on AnimDie skiplist — running vanilla StartDeathAnim immediately");
                ScheduleTargetingResetAfterTransition(__instance, typeName);
                return true; // run original synchronously
            }

            // Revive-power creatures pass through to vanilla AnimDie (see
            // AnimDiePatch.Prefix). Vanilla's StartDeathAnim → AnimDie chain
            // expects synchronous timing — the 0.2s defer would desync the
            // power state machine (IllusionPower's <ReviveMove>) from the
            // death visuals. Run vanilla immediately, no defer.
            if (HasReviveLikePower(__instance) is { } powerName)
            {
                RestoreLogger.Warn($"[DeathDefer] creature has revive-like power '{powerName}' instId={__instance.GetInstanceId()} — running vanilla StartDeathAnim immediately");
                return true; // run original synchronously
            }

            ulong id = __instance.GetInstanceId();
            // If this creature already has a pending defer, drop the new call —
            // game shouldn't double-trigger StartDeathAnim, but if it does we
            // honor the first one (with its original arg).
            if (_pending.ContainsKey(id)) return false;

            var timer = new Godot.Timer
            {
                OneShot = true,
                WaitTime = DeferSeconds,
            };
            __instance.AddChild(timer);

            // Capture by id, not by closure, so we can resolve the dict entry
            // at firing time even if external code re-keyed things.
            timer.Timeout += () => OnDeferredFire(id);

            _pending[id] = (timer, __instance, __0);
            timer.Start();

            RestoreLogger.Info($"[DeathDefer] deferred StartDeathAnim on creature " +
                $"instId={id} arg={__0} for {DeferSeconds:F2}s");
            return false; // skip original; the timer will re-call it
        }
        catch (Exception ex)
        {
            RestoreLogger.Warn($"[DeathDefer] defer setup failed: {ex.Message} — letting original run");
            return true;
        }
    }

    private static void OnDeferredFire(ulong id)
    {
        if (!_pending.TryGetValue(id, out var entry)) return;
        _pending.Remove(id);

        try { entry.timer.QueueFree(); } catch { }

        if (!Godot.GodotObject.IsInstanceValid(entry.creature))
        {
            RestoreLogger.Info($"[DeathDefer] timer fired but creature instId={id} freed — skipping");
            return;
        }

        RestoreLogger.Info($"[DeathDefer] timer fired — running real StartDeathAnim on instId={id}");
        _bypass = true;
        try { entry.creature.StartDeathAnim(entry.arg); }
        catch (Exception ex) { RestoreLogger.Warn($"[DeathDefer] deferred StartDeathAnim threw: {ex.Message}"); }
        finally { _bypass = false; }
    }

    /// <summary>
    /// Undo entry — called from <c>SnapshotRestorer.Restore</c>. Cancels every
    /// pending death-anim timer so undo within the defer window prevents
    /// AnimDie from ever starting — the creature stays fully intact in the
    /// scene tree and the model-level revive is effectively a no-op (creature
    /// was never actually removed from cs.Creatures via the death path).
    /// Creatures are left ALIVE on purpose.
    /// </summary>
    public static int AbortAllPending()
    {
        if (_pending.Count == 0) return 0;
        int aborted = 0;
        foreach (var kv in _pending.ToList())
        {
            try
            {
                if (Godot.GodotObject.IsInstanceValid(kv.Value.timer))
                {
                    kv.Value.timer.Stop();
                    kv.Value.timer.QueueFree();
                }
                aborted++;
            }
            catch { }
        }
        _pending.Clear();
        RestoreLogger.Info($"[DeathDefer] aborted {aborted} pending death anim(s) for undo");
        return aborted;
    }

    /// <summary>
    /// Combat-end entry — called from <c>PatchEndCombatInternal</c>. Single-enemy
    /// fights (and last-kill of any fight) trigger combat-end within ~13ms of the
    /// killing blow, well before our <see cref="DeferSeconds"/> timer fires. If we
    /// just QueueFree'd here the player would see the enemy vanish with no death
    /// anim at all — the killing blow on every solo encounter looked broken.
    ///
    /// Instead, fire the deferred <c>StartDeathAnim</c> immediately with the bypass
    /// flag so our replacement AnimDie (spine 'die' + 0.6s + detach) runs under the
    /// win banner. Visually noisier than a clean cut, but the player wanted the
    /// animation to play.
    /// </summary>
    public static int FlushForCombatEnd()
    {
        if (_pending.Count == 0) return 0;
        int flushed = 0;
        foreach (var kv in _pending.ToList())
        {
            try
            {
                if (Godot.GodotObject.IsInstanceValid(kv.Value.timer))
                {
                    kv.Value.timer.Stop();
                    kv.Value.timer.QueueFree();
                }
                if (Godot.GodotObject.IsInstanceValid(kv.Value.creature))
                {
                    _bypass = true;
                    try { kv.Value.creature.StartDeathAnim(kv.Value.arg); }
                    catch (Exception ex)
                    { RestoreLogger.Warn($"[DeathDefer] flush StartDeathAnim threw on instId={kv.Key}: {ex.Message}"); }
                    finally { _bypass = false; }
                }
                flushed++;
            }
            catch { }
        }
        _pending.Clear();
        RestoreLogger.Info($"[DeathDefer] flushed {flushed} pending death(s) for combat-end (anim allowed to run)");
        return flushed;
    }

    /// <summary>
    /// Reset entry (combat init) — called from <c>PatchCombatReset</c> to drop
    /// any state left over from a prior combat. By the time Reset runs the
    /// scene-level NCreature nodes are typically already gone, so we only
    /// need to zero the dictionary; defensively stop any lingering timers.
    /// </summary>
    public static void ClearAll() => AbortAllPending();

    /// <summary>
    /// Best-effort `monster.GetType().Name` — null on any chain break. Mirrors
    /// AnimDiePatch.GetMonsterTypeName but kept private here so this patch
    /// stays self-contained.
    /// </summary>
    private static string? TryGetMonsterTypeName(NCreature creature)
    {
        try
        {
            var monster = creature?.Entity?.Monster;
            return monster?.GetType().Name;
        }
        catch { return null; }
    }

    /// <summary>
    /// True if any of this creature's powers has a class name matching the
    /// revive-like substring set (Revive / Reborn / Reincarn / PreventDeath /
    /// InvincibleOnDeath). Returns the matching name for logging, or null.
    /// </summary>
    // Keep in sync with AnimDiePatch.ReviveLikePowerNameSubstrings — both gates
    // need to agree, otherwise vanilla's StartDeathAnim → AnimDie chain ends up
    // with one half deferred (0.2s timer here) and the other half synchronous
    // (vanilla AnimDie via AnimDiePatch pass-through), which desyncs the power
    // state machine from the death visuals. Reported 2026-05-08 for Osty:
    // idle anim kept playing during the dying-pre-revive window because this
    // list missed "DieForYou".
    private static readonly string[] _reviveLikeSubstrings =
        { "Revive", "Reborn", "Reincarn", "PreventDeath", "InvincibleOnDeath", "Illusion", "DieForYou" };

    private static string? HasReviveLikePower(NCreature creature)
    {
        try
        {
            var entity = creature?.Entity;
            if (entity == null) return null;
            foreach (var pm in entity.Powers)
            {
                if (pm == null) continue;
                var name = pm.GetType().Name;
                foreach (var sub in _reviveLikeSubstrings)
                {
                    if (name.IndexOf(sub, StringComparison.Ordinal) >= 0) return name;
                }
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Wait for vanilla's phase transition to settle, then force-clear the
    /// `IsFocused` backing field on the NCreature. If the player landed the
    /// killing blow with the mouse over the creature (very common — they just
    /// played an attack card), MouseEntered → OnFocus set IsFocused = true and
    /// vanilla's transition swap can leave that state stuck on the same
    /// NCreature node when phase-2 reuses it. Stuck IsFocused makes single-
    /// target hover skip NTargetManager.OnNodeHovered (AOE bypasses hover so
    /// it still works) — same symptom and fix as the post-revive path in
    /// `CreatureVisualRefresher.ResetTargetingState`.
    /// </summary>
    private const float TargetingResetDelaySeconds = 1.5f;
    private const float RetryDelaySeconds = 1.0f;
    private const int MaxRetries = 3;

    private static void ScheduleTargetingResetAfterTransition(NCreature creature, string typeName)
    {
        try
        {
            var tree = creature.GetTree();
            if (tree == null)
            {
                RestoreLogger.Warn($"[DeathDefer] no SceneTree on {typeName} — skipping post-transition targeting reset");
                return;
            }
            var rootCapture = tree.Root;
            ScheduleSweep(tree, rootCapture, typeName, TargetingResetDelaySeconds, MaxRetries);
            RestoreLogger.Warn($"[DeathDefer] scheduled post-transition sweep for {typeName} in {TargetingResetDelaySeconds:F1}s (up to {MaxRetries} retries)");
        }
        catch (Exception ex)
        {
            RestoreLogger.Warn($"[DeathDefer] failed to schedule targeting reset for {typeName}: {ex.Message}");
        }
    }

    private static void ScheduleSweep(Godot.SceneTree tree, Godot.Node root, string typeName, float delay, int retriesLeft)
    {
        try
        {
            var timer = tree.CreateTimer(delay);
            timer.Timeout += () =>
            {
                int touched = 0;
                int deadSkipped = 0;
                ResetIsFocusedOnAllLiveCreatures(root, typeName, ref touched, ref deadSkipped);
                RestoreLogger.Warn($"[DeathDefer] sweep done for {typeName} — touched={touched} deadSkipped={deadSkipped} retriesLeft={retriesLeft}");
                // If a creature was dead-skipped (vanilla phase-2 swap not done
                // yet), retry — phase-2 spawn timing is data-dependent and the
                // initial 1.5s isn't always enough.
                if (deadSkipped > 0 && retriesLeft > 0 && Godot.GodotObject.IsInstanceValid(tree))
                {
                    ScheduleSweep(tree, root, typeName, RetryDelaySeconds, retriesLeft - 1);
                }
            };
        }
        catch (Exception ex)
        {
            RestoreLogger.Warn($"[DeathDefer] ScheduleSweep failed for {typeName}: {ex.Message}");
        }
    }

    private static void ResetIsFocusedOnAllLiveCreatures(Godot.Node root, string typeName, ref int touched, ref int deadSkipped)
    {
        try
        {
            if (!Godot.GodotObject.IsInstanceValid(root))
            {
                RestoreLogger.Warn($"[DeathDefer] root invalid at sweep time for {typeName} — skip");
                return;
            }
            CollectAndReset(root, ref touched, ref deadSkipped);
        }
        catch (Exception ex)
        {
            RestoreLogger.Warn($"[DeathDefer] post-transition sweep failed for {typeName}: {ex.Message}");
        }
    }

    private static readonly System.Reflection.FieldInfo?[] _isFocusedFields = ResolveIsFocusedFields();

    private static System.Reflection.FieldInfo?[] ResolveIsFocusedFields()
    {
        var found = new List<System.Reflection.FieldInfo?>();
        foreach (var fieldName in new[] { "<IsFocused>k__BackingField", "_isFocused", "isFocused" })
        {
            var f = HarmonyLib.AccessTools.Field(typeof(NCreature), fieldName);
            if (f != null) found.Add(f);
        }
        return found.ToArray();
    }

    private static IEnumerable<Godot.Node> WalkDescendants(Godot.Node root)
    {
        if (!Godot.GodotObject.IsInstanceValid(root)) yield break;
        var stack = new Stack<Godot.Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (!Godot.GodotObject.IsInstanceValid(n)) continue;
            yield return n;
            foreach (var c in n.GetChildren())
            {
                if (c is Godot.Node child) stack.Push(child);
            }
        }
    }

    private static void CollectAndReset(Godot.Node node, ref int touched, ref int deadSkipped)
    {
        if (!Godot.GodotObject.IsInstanceValid(node)) return;
        if (node is NCreature nc)
        {
            // Determine alive-ness. The StateDisplay restore is gated on this:
            // if vanilla hasn't completed phase-2 swap yet, Entity is still the
            // dying phase-1 monster (HP=0), and force-showing + RefreshValues
            // would lock the bar at "0/DEAD" until something else repaints it.
            // Skip and let a retry sweep handle it.
            bool isAlive = false;
            try
            {
                var entity = nc.Entity;
                if (entity != null && !entity.IsDead) isAlive = true;
            }
            catch { /* Entity access can throw mid-transition; treat as not alive */ }

            // 1) IsFocused backing field — same as revive path / single-target hover.
            //    Always reset, regardless of alive/dead, so post-transition
            //    targeting works even if we're still on a retry.
            foreach (var f in _isFocusedFields)
            {
                try { f?.SetValue(nc, false); } catch { }
            }
            // 2) Hitbox state — vanilla AnimDie may have flipped MouseFilter to
            //    Ignore / FocusMode to None during the death anim and not
            //    restored them when phase-2 reused the same NCreature node.
            //    Mirrors SnapshotRestorer.cs:738-742 (revive path). Only restore
            //    on alive creatures — corpses should keep their hidden hitbox.
            if (isAlive)
            {
                try
                {
                    if (nc.Hitbox != null)
                    {
                        var prevMf = nc.Hitbox.MouseFilter;
                        var prevFm = nc.Hitbox.FocusMode;
                        var prevVis = nc.Hitbox.Visible;
                        nc.Hitbox.MouseFilter = Godot.Control.MouseFilterEnum.Stop;
                        nc.Hitbox.FocusMode = Godot.Control.FocusModeEnum.All;
                        nc.Hitbox.Visible = true;
                        if (prevMf != Godot.Control.MouseFilterEnum.Stop
                            || prevFm != Godot.Control.FocusModeEnum.All
                            || !prevVis)
                        {
                            RestoreLogger.Warn($"[DeathDefer] hitbox-restore instId={nc.GetInstanceId()} mf {prevMf}->Stop fm {prevFm}->All vis {prevVis}->True");
                        }
                    }
                }
                catch (Exception ex)
                {
                    RestoreLogger.Warn($"[DeathDefer] hitbox restore failed instId={nc.GetInstanceId()}: {ex.Message}");
                }
                // 3) ToggleIsInteractable(true) — same belt-and-braces as revive path.
                try { nc.ToggleIsInteractable(nc.Entity?.Monster?.IsHealthBarVisible ?? true); }
                catch (Exception ex)
                { RestoreLogger.Warn($"[DeathDefer] ToggleIsInteractable failed instId={nc.GetInstanceId()}: {ex.Message}"); }
            }

            // 4) NCreatureStateDisplay restore — `AnimateOut` runs on death and
            //    leaves the StateDisplay (parent of HP bar / nameplate / power
            //    container) Visible=false, Modulate.A=0. Phase-2 reuses the
            //    NCreature node but vanilla doesn't reverse AnimateOut, so the
            //    HP bar stays hidden after the transition. Mirrors the revive
            //    path in CreatureVisualRefresher.cs:248-280 (alive branch).
            //
            //    GUARD: only force-show on ALIVE creatures. Force-showing on a
            //    dying/dead creature would call RefreshValues with HP=0 and
            //    paint "DEAD" into the bar, which then sticks until something
            //    else triggers a repaint (briefly visible 0/DEAD bar). When
            //    dead, we keep the AnimateOut state (hidden) and rely on the
            //    retry sweep to catch phase-2 once it's actually alive.
            if (isAlive)
            {
                try
                {
                    int restored = 0;
                    foreach (var d in WalkDescendants(nc))
                    {
                        if (d is not MegaCrit.Sts2.Core.Nodes.Combat.NCreatureStateDisplay sd) continue;
                        var mod = sd.Modulate;
                        bool wasHidden = !sd.Visible || mod.A < 1f;
                        sd.Visible = true;
                        mod.A = 1f;
                        sd.Modulate = mod;
                        try { ReflectionCache.NCreatureStateDisplayRefreshValuesMethod?.Invoke(sd, null); }
                        catch (Exception rex)
                        { RestoreLogger.Warn($"[DeathDefer] StateDisplay.RefreshValues failed: {rex.Message}"); }
                        if (wasHidden)
                            RestoreLogger.Warn($"[DeathDefer] StateDisplay un-hidden (alive) instId={nc.GetInstanceId()}");
                        restored++;
                    }
                    if (restored == 0)
                        RestoreLogger.Warn($"[DeathDefer] no NCreatureStateDisplay found under instId={nc.GetInstanceId()}");
                }
                catch (Exception ex)
                {
                    RestoreLogger.Warn($"[DeathDefer] StateDisplay restore failed instId={nc.GetInstanceId()}: {ex.Message}");
                }
            }
            else
            {
                // Phase-2 swap not yet complete (or this is genuinely a dead
                // corpse). Don't touch StateDisplay — vanilla's AnimateOut state
                // is correct visuals. Bump deadSkipped so the caller can retry.
                deadSkipped++;
                RestoreLogger.Warn($"[DeathDefer] StateDisplay kept hidden (Entity dead/null) instId={nc.GetInstanceId()} — will retry");
            }

            touched++;
        }
        foreach (var child in node.GetChildren())
        {
            if (child is Godot.Node n) CollectAndReset(n, ref touched, ref deadSkipped);
        }
    }
}
