using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Orbs;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using WorldLineYggdrasil.Restore.Patches;
using WorldLineYggdrasil.Restore.Visuals;
using System.Reflection;

namespace WorldLineYggdrasil.Restore;

/// <summary>
/// Apply a captured CombatSnapshot back to live state. Each restore step is wrapped
/// in try/catch so a failure in one section (e.g. a missing field after a game update)
/// doesn't tank the entire undo — partial restores are still useful for the player.
///
/// Order matters:
///   1. Data layer first — HP, energy, piles, powers, etc. mutate the model.
///   2. Visual layer second — once data is correct, refresh nodes to match.
/// </summary>
internal static class SnapshotRestorer
{
    public static void Restore(CombatSnapshot snap)
    {
        var cm = CombatManager.Instance;
        if (cm == null) { RestoreLogger.Warn("[Restore] CombatManager null"); return; }
        var cs = ReflectionCache.GetCombatState(cm);
        if (cs == null) { RestoreLogger.Warn("[Restore] CombatState null"); return; }
        var runState = ReflectionCache.RunManagerStateProperty?.GetValue(RunManager.Instance) as RunState;
        if (runState == null) { RestoreLogger.Warn("[Restore] RunState null"); return; }

        RestoreLogger.Info($"[Restore] start → round={snap.RoundNumber} side={snap.CurrentSide} " +
            $"creatures={snap.Creatures.Count} (live cs.Creatures.Count={cs.Creatures.Count})");

        // FIRST step before any data restore: abort any pending deferred-death
        // timers (see DeathAnimDelayPatch). If a creature was just killed and
        // the player undid within the defer window, AnimDie has not started
        // yet — aborting the timer keeps the body fully intact so revive is a
        // no-op visually. Without this, the timer would fire mid-restore and
        // start AnimDie on the freshly-revived creature.
        try { DeathAnimDelayPatch.AbortAllPending(); }
        catch (Exception ex) { RestoreLogger.Warn($"[Restore] DeathDefer abort failed: {ex.Message}"); }

        // ── DATA LAYER ──
        Try("CombatLevel", () => RestoreCombatLevel(snap, cs));
        Try("Roster",      () => RestoreCreatureRoster(snap, cs));   // re-add removed creatures FIRST
        Try("Player",      () => RestorePlayerAndPiles(snap, cs));
        Try("Creatures",   () => RestoreCreatures(snap, cs));
        Try("Relics",      () => RestoreRelics(snap, cs));
        Try("Orbs",        () => RestoreOrbs(snap, cs));
        Try("Potions",     () => RestorePotions(snap, cs));
        Try("RNG",         () => RestoreRunRng(snap, runState));
        Try("History",     () => RestoreHistory(snap, cm));
        Try("SyncState",   () => RestoreSyncState(snap));

        // ── VISUAL LAYER ──
        // Sweep ephemeral VFX nodes (card-flying, damage particles, hit numbers)
        // FIRST so they don't survive into the rebuilt scene. We capture the
        // baseline scene-node set on snapshot; anything new at restore time is
        // ephemeral.
        Try("EphemeralNodes",  () => EphemeralNodeCleaner.Clean(snap));
        // Targeted VFX cleanup — splash + liquid-overlay nodes added by
        // NCombatRoom.PlaySplashVfx live as standalone scene nodes under
        // CombatVfxContainer and self-free after ~0.5s. Without immediate
        // cleanup, undoing right after a goopy/poison hit leaves the striped
        // overlay graphic floating over the target until the timer expires.
        Try("CombatVfx",       () => CleanCombatVfxContainers());
        // Clear lingering card-target reticles. NSelectionReticle.OnSelect
        // is called when the player drags a target-required card over an
        // enemy; OnDeselect (which fades modulate.a to 0) only fires on a
        // proper drag-end. Undo bypasses the drag lifecycle entirely, so
        // the reticle stays at modulate=White / scale=1 (IsSelected=True)
        // and the user sees a permanent target outline around the enemy.
        Try("DeselectReticles", () => ClearTargetingReticles());
        Try("CreatureVisuals", () => CreatureVisualRefresher.Refresh(snap));
        // Orphan cleanup BEFORE HandRefresher: hand-add path keys off
        // holder.CardNode, but a card mid-lift has been reparented out of its
        // holder. Without pre-cleanup, HandRefresher creates a duplicate NCard
        // and the old animating one orphans.
        Try("OrphanCards",     () => OrphanCardCleaner.Clean());
        Try("HandVisuals",     () => HandRefresher.Refresh(snap));
        // Snap holders to their target position/angle/scale immediately so undo
        // appears as a state replacement rather than a tweened animation.
        Try("SnapHand",        () => HandPositionSnapper.Snap());
        Try("PowerVisuals",    () => PowerRefresher.Refresh(snap));
        Try("OrbVisuals",      () => OrbRefresher.Refresh(snap));
        Try("PotionVisuals",   () => PotionRefresher.Refresh(snap));
        Try("PileCounts",      () => PileCountRefresher.Refresh(snap));
        Try("EndTurnState",    () => EndTurnStateRefresher.Reset());
        Try("Diag",            () => InputStateDiagnostics.Dump());

        // Tell the game's state tracker to push energy/HP/block displays.
        Try("StateTracker", () =>
        {
            ReflectionCache.NotifyCombatStateChangedMethod?.Invoke(
                cm.StateTracker, new object[] { "Sts2UndoMod" });
        });

        // Fire CombatManager.TurnStarted so end-turn button + UI bindings refresh.
        Try("FireTurnStarted", () =>
        {
            var ev = AccessTools.Field(typeof(CombatManager), "TurnStarted");
            if (ev?.GetValue(cm) is Delegate d) d.DynamicInvoke(cs);
        });

        RestoreLogger.Info("[Restore] complete");
    }

    /// <summary>
    /// Free in-flight combat VFX nodes (splash + liquid-overlay) that the
    /// game's PlaySplashVfx pathway parents under CombatVfxContainer /
    /// BackCombatVfxContainer. These nodes self-free after their async timer
    /// (~0.5s) but undo lands immediately, leaving the striped goopy graphic
    /// floating over a now-restored creature until the unrelated timer ticks
    /// down. Free anything inside the containers — they're scoped to combat
    /// VFX and shouldn't hold structural UI.
    /// </summary>
    private static void CleanCombatVfxContainers()
    {
        var room = MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom.Instance;
        if (room == null) { RestoreLogger.Info("[CombatVfx] NCombatRoom.Instance==null, skip"); return; }
        int freed = 0;
        var perContainer = new List<string>();
        foreach (var name in new[] { "CombatVfxContainer", "BackCombatVfxContainer" })
        {
            try
            {
                var prop = HarmonyLib.AccessTools.Property(typeof(MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom), name);
                if (prop?.GetValue(room) is not Godot.Node container)
                {
                    perContainer.Add($"{name}=<null>");
                    continue;
                }
                var children = new List<Godot.Node>();
                foreach (Godot.Node ch in container.GetChildren()) children.Add(ch);
                int containerFreed = 0;
                var typeNames = new List<string>();
                foreach (var ch in children)
                {
                    try
                    {
                        var typeName = ch.GetType().Name;
                        if (typeNames.Count < 8) typeNames.Add(typeName);
                        if (Godot.GodotObject.IsInstanceValid(ch)) { ch.QueueFree(); containerFreed++; }
                    }
                    catch { }
                }
                freed += containerFreed;
                perContainer.Add($"{name}={children.Count}({string.Join(",", typeNames)})");
            }
            catch (Exception ex) { RestoreLogger.Warn($"[CombatVfx] sweep {name}: {ex.Message}"); }
        }
        RestoreLogger.Info($"[CombatVfx] freed={freed} | {string.Join(" | ", perContainer)}");

        // The standard combat VFX containers were empty in observed reports —
        // the goopy/striped graphic must be coming from elsewhere. Walk every
        // live NCreature's subtree and free any descendant whose type name
        // looks like a VFX/Effect overlay (NLiquidOverlayVfx, NSplashVfx,
        // N*PowerAddedVfx, N*EnchantVfx, N*Effect…). We deliberately skip
        // structural Visuals/Body/HpBar/etc. by requiring the type name to
        // contain "Vfx" or end with "Effect". Logged with full inventory.
        try
        {
            int creatureFreed = 0;
            var typesFreed = new HashSet<string>();
            foreach (var ncreature in MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom.Instance!.CreatureNodes)
            {
                if (ncreature == null || !Godot.GodotObject.IsInstanceValid(ncreature)) continue;
                foreach (var node in CombatSnapshot.EnumerateTree(ncreature))
                {
                    if (node == null || node == ncreature) continue;
                    var t = node.GetType().Name;
                    if (!t.Contains("Vfx") && !t.EndsWith("Effect")) continue;
                    try
                    {
                        node.QueueFree();
                        creatureFreed++;
                        typesFreed.Add(t);
                    }
                    catch { }
                }
            }
            if (creatureFreed > 0)
                RestoreLogger.Info($"[CreatureVfx] freed={creatureFreed} types=[{string.Join(",", typesFreed)}]");
            else
            {
                // Dump per-creature type bucket so we can spot what overlay
                // class is sitting on the sprite if our heuristic missed it.
                foreach (var ncreature in MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom.Instance.CreatureNodes)
                {
                    if (ncreature == null || !Godot.GodotObject.IsInstanceValid(ncreature)) continue;
                    var buckets = new Dictionary<string, int>();
                    foreach (var node in CombatSnapshot.EnumerateTree(ncreature))
                    {
                        if (node == null) continue;
                        var t = node.GetType().Name;
                        buckets.TryGetValue(t, out var c); buckets[t] = c + 1;
                    }
                    var top = string.Join(",",
                        buckets.OrderByDescending(kv => kv.Value).Take(15)
                               .Select(kv => $"{kv.Key}={kv.Value}"));
                    RestoreLogger.Info($"[CreatureVfx] dump entity='{ncreature.Entity?.Name?.ToString() ?? "?"}' types: {top}");
                }
            }
        }
        catch (Exception ex) { RestoreLogger.Warn($"[CreatureVfx] sweep failed: {ex.Message}"); }
    }

    /// <summary>
    /// After in-place revive, walk the NCreature's subtree, find each
    /// NSelectionReticle, and replace its private `_cancelToken` field with
    /// a fresh CancellationTokenSource. See call site for full context —
    /// in short, _ExitTree on the dying creature cancelled the existing
    /// token, leaving OnDeselect inert until we replace it.
    /// </summary>
    private static void ResetReticleCancelTokens(Godot.Node ncreature, uint combatId)
    {
        try
        {
            var reticleType = HarmonyLib.AccessTools.TypeByName(
                "MegaCrit.Sts2.Core.Nodes.Combat.NSelectionReticle");
            if (reticleType == null) return;
            var ctsField = HarmonyLib.AccessTools.Field(reticleType, "_cancelToken");
            if (ctsField == null) return;
            int reset = 0;
            foreach (var node in CombatSnapshot.EnumerateTree(ncreature))
            {
                if (node == null || !reticleType.IsInstanceOfType(node)) continue;
                try
                {
                    ctsField.SetValue(node, new System.Threading.CancellationTokenSource());
                    reset++;
                }
                catch { }
            }
            if (reset > 0) RestoreLogger.Info($"[Revive] id={combatId} reset {reset} reticle CancellationTokenSource(s)");
        }
        catch (Exception ex) { RestoreLogger.Warn($"[Revive] id={combatId} reticle CTS reset: {ex.Message}"); }
    }

    /// <summary>
    /// Walk every live NCreature, find its NSelectionReticle child(ren), and
    /// invoke OnDeselect (or directly zero the modulate if call fails). This
    /// is what the game does at the end of a successful card drag; undo
    /// short-circuits that lifecycle so we have to call it ourselves.
    /// </summary>
    private static void ClearTargetingReticles()
    {
        var room = MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom.Instance;
        if (room == null) return;
        int cleared = 0;
        var reticleType = HarmonyLib.AccessTools.TypeByName(
            "MegaCrit.Sts2.Core.Nodes.Combat.NSelectionReticle");
        var onDeselect = reticleType != null
            ? HarmonyLib.AccessTools.Method(reticleType, "OnDeselect")
            : null;
        var isSelectedSetter = reticleType != null
            ? HarmonyLib.AccessTools.PropertySetter(reticleType, "IsSelected")
            : null;
        foreach (var ncreature in room.CreatureNodes)
        {
            if (ncreature == null || !Godot.GodotObject.IsInstanceValid(ncreature)) continue;
            foreach (var node in CombatSnapshot.EnumerateTree(ncreature))
            {
                if (node == null || reticleType == null || !reticleType.IsInstanceOfType(node)) continue;
                try
                {
                    onDeselect?.Invoke(node, null);
                    if (node is Godot.CanvasItem ci) ci.Modulate = Godot.Colors.Transparent;
                    isSelectedSetter?.Invoke(node, new object[] { false });
                    cleared++;
                }
                catch (Exception ex)
                { RestoreLogger.Warn($"[Reticle] clear: {ex.Message}"); }
            }
        }
        if (cleared > 0) RestoreLogger.Info($"[Reticle] cleared {cleared} targeting reticle(s)");
    }

    private static void Try(string name, Action action)
    {
        try { action(); }
        catch (Exception ex) { RestoreLogger.Warn($"[Restore] {name} failed: {ex.Message}"); }
    }

    // ─── Data restore steps ───

    private static void RestoreCombatLevel(CombatSnapshot snap, CombatState cs)
    {
        // RoundNumber + CurrentSide are public properties with private setters
        // — we use AccessTools to set their backing fields if direct set fails.
        TrySetProperty(cs, nameof(CombatState.RoundNumber), snap.RoundNumber);
        TrySetProperty(cs, nameof(CombatState.CurrentSide), snap.CurrentSide);

        if (ReflectionCache.NextCreatureIdField != null)
            ReflectionCache.NextCreatureIdField.SetValue(cs, snap.NextCreatureId);
    }

    private static void RestorePlayerAndPiles(CombatSnapshot snap, CombatState cs)
    {
        foreach (var ally in cs.Allies)
        {
            var player = ally.Player;
            if (player == null) continue;
            var pcs = player.PlayerCombatState;
            if (pcs != null)
            {
                ReflectionCache.PcsEnergyField.SetValue(pcs, snap.Energy);
                ReflectionCache.PcsStarsField.SetValue(pcs, snap.Stars);

                // TurnNumber is auto-property with private setter; restore it so
                // turn-counting relic/power effects (Candelabra, Chandelier,
                // Happy Flower, ...) fire correctly after a cross-turn restore.
                try
                {
                    if (ReflectionCache.PcsTurnNumberSetter != null && snap.TurnNumber > 0)
                    {
                        ReflectionCache.PcsTurnNumberSetter.Invoke(pcs, new object[] { snap.TurnNumber });
                    }
                }
                catch (Exception ex)
                {
                    RestoreLogger.Warn($"[Restore] TurnNumber restore failed: {ex.Message}");
                }

                RestoreCardPiles(snap, pcs);
                RestoreCardMutableState(snap);
                LogHandLocalModifiers(pcs, "after restore");
            }
            ReflectionCache.PlayerGoldField?.SetValue(player, snap.Gold);
            break;
        }
    }

    /// <summary>
    /// Diagnostic: dump each hand card's `_localModifiers` list size + cost,
    /// so the log shows whether Pounce/Unrelenting-style local cost modifiers
    /// survive capture+restore. Only emits a line when at least one hand card
    /// has a non-empty modifier list (most plays don't apply local modifiers).
    /// </summary>
    private static void LogHandLocalModifiers(PlayerCombatState pcs, string label)
    {
        if (ReflectionCache.CardEnergyCostLocalModifiersField == null) return;
        try
        {
            CardPile? hand = null;
            foreach (var pile in pcs.AllPiles)
                if (pile.Type == PileType.Hand) { hand = pile; break; }
            if (hand == null) return;

            var entries = new List<string>();
            foreach (var card in hand.Cards)
            {
                var energyCost = ReflectionCache.CardEnergyCostProp?.GetValue(card);
                if (energyCost == null) continue;
                var mods = ReflectionCache.CardEnergyCostLocalModifiersField.GetValue(energyCost)
                    as System.Collections.IList;
                int n = mods?.Count ?? 0;
                if (n == 0) continue;
                entries.Add($"{card.Id.Entry}(mods={n})");
            }
            if (entries.Count > 0)
                RestoreLogger.Info($"[CardMods] hand {label}: [{string.Join(", ", entries)}]");
        }
        catch (Exception ex) { RestoreLogger.Warn($"[CardMods] log {label}: {ex.Message}"); }
    }

    private static void RestoreCardPiles(CombatSnapshot snap, PlayerCombatState pcs)
    {
        // Replace each pile's _cards content with the saved list. Cards are SAME
        // refs as before so subscriptions on individual CardModel instances stay
        // valid. Cards that moved between piles get reordered correctly.
        foreach (var pile in pcs.AllPiles)
        {
            if (!snap.PileRefs.TryGetValue(pile.Type, out var savedCards)) continue;

            var liveCards = ReflectionCache.CardPileCardsField.GetValue(pile)
                as System.Collections.IList;
            if (liveCards == null) continue;

            liveCards.Clear();
            foreach (var c in savedCards) liveCards.Add(c);

            // Fire ContentsChanged so subscribers update.
            var contentsChanged = AccessTools.Field(typeof(CardPile), "ContentsChanged");
            (contentsChanged?.GetValue(pile) as Delegate)?.DynamicInvoke();
        }
    }

    private static void RestoreCardMutableState(CombatSnapshot snap)
    {
        // For each captured card, copy non-identity fields from its clone back
        // onto the live card. This restores per-card state (cost overrides,
        // calculated vars, keyword flags) without breaking the card's identity.
        foreach (var (live, clone) in snap.CardMutableClones)
        {
            foreach (var field in ReflectionCache.CardMutableFields)
            {
                try
                {
                    var v = field.GetValue(clone);
                    field.SetValue(live, v);
                }
                catch { /* skip individual unsettable fields */ }
            }

            // ── Back-reference fixups ──
            // After copying _energyCost / _dynamicVars from clone, both objects
            // still hold pointers back to the CLONE (not live). Cost modifier
            // checks read clone.CombatState (null) and silently no-op, so
            // power cards fail to apply. Repoint to live.
            FixCardBackReferences(live);
        }
    }

    private static void FixCardBackReferences(CardModel live)
    {
        try
        {
            // EnergyCost._card = live
            var energyCost = ReflectionCache.CardEnergyCostProp?.GetValue(live);
            if (energyCost != null && ReflectionCache.EnergyCostCardField != null)
                ReflectionCache.EnergyCostCardField.SetValue(energyCost, live);
        }
        catch (Exception ex) { RestoreLogger.Warn($"[Card] EnergyCost back-ref fix: {ex.Message}"); }

        try
        {
            // DynamicVars.InitializeWithOwner(live) — fixes per-CalculatedVar owners.
            var dyn = ReflectionCache.CardDynamicVarsProp?.GetValue(live);
            if (dyn != null && ReflectionCache.DynamicVarsInitializeWithOwnerMethod != null)
                ReflectionCache.DynamicVarsInitializeWithOwnerMethod.Invoke(dyn, new object[] { live });
        }
        catch (Exception ex) { RestoreLogger.Warn($"[Card] DynamicVars back-ref fix: {ex.Message}"); }

        try
        {
            // Enchantment._card = live. Cloned via EnchantInternal during DeepCloneFields,
            // so it points at the snapshot clone whose CombatState/Owner is null.
            var ench = ReflectionCache.CardEnchantmentProp?.GetValue(live);
            if (ench != null && ReflectionCache.EnchantmentCardField != null)
                ReflectionCache.EnchantmentCardField.SetValue(ench, live);
        }
        catch (Exception ex) { RestoreLogger.Warn($"[Card] Enchantment back-ref fix: {ex.Message}"); }

        try
        {
            // Affliction._card = live. Same pattern as Enchantment.
            var afflict = ReflectionCache.CardAfflictionProp?.GetValue(live);
            if (afflict != null && ReflectionCache.AfflictionCardField != null)
                ReflectionCache.AfflictionCardField.SetValue(afflict, live);
        }
        catch (Exception ex) { RestoreLogger.Warn($"[Card] Affliction back-ref fix: {ex.Message}"); }
    }

    /// <summary>
    /// Two-way roster sync. Runs BEFORE per-creature data restore.
    ///   REMOVE: live creature not in snap → remove + free visual (summoned-then-undone case)
    ///   REVIVE: snap creature missing from live → full revive path that goes through
    ///           CombatManager.AddCreature so the target manager registers it and
    ///           the player can click on the revived creature.
    /// </summary>
    private static void RestoreCreatureRoster(CombatSnapshot snap, CombatState cs)
    {
        if (ReflectionCache.CsAlliesField.GetValue(cs)
            is not System.Collections.IList alliesList) return;
        if (ReflectionCache.CsEnemiesField.GetValue(cs)
            is not System.Collections.IList enemiesList) return;

        var snapIds = new HashSet<uint>();
        foreach (var s in snap.Creatures) snapIds.Add(s.CombatId);

        int removed = RemoveCreaturesNotIn(snapIds, alliesList);
        removed   += RemoveCreaturesNotIn(snapIds, enemiesList);

        var liveIds = new HashSet<uint>();
        foreach (var c in cs.Creatures)
            if (c.CombatId.HasValue) liveIds.Add(c.CombatId.Value);

        int revived = 0;
        foreach (var saved in snap.Creatures)
        {
            if (saved.Ref == null) continue;
            if (liveIds.Contains(saved.CombatId)) continue;
            // Skip dead-in-snap creatures. They were captured WHILE dead
            // (mid-AnimDie, before cs.Creatures.Remove ran). After AnimDie
            // completed they were removed from cs.Creatures and their
            // NCreature was detached by AnimDiePatch. We must NOT revive:
            // the snapshot says they were dead, undo should leave them dead.
            // Reviving here resurrected enemies the user had already killed
            // (in-place revive + StartReviveAnim made them visually alive).
            // The 만각지네 corpse-with-revive-intent case is handled
            // separately because those creatures stay in cs.Creatures with
            // IsDead=true (they hit the liveIds.Contains continue above).
            if (saved.IsDead) continue;
            if (ReviveCreature(saved, cs)) revived++;
        }

        if (revived + removed > 0)
        {
            RestoreLogger.Info($"[Roster] revived={revived} removed={removed}");
            var changed = AccessTools.Field(typeof(CombatState), "CreaturesChanged");
            try { (changed?.GetValue(cs) as Delegate)?.DynamicInvoke(cs); } catch { }
        }

        // NOTE: FullEnemyVisualRebuild path tried (CreateAllyNodes + CreateEnemyNodes
        // after wiping all NCreature nodes). Result: every fresh node ended up
        // in-tree with correct transforms but NOTHING rendered, including the
        // alive ones that were rendering fine before. CreateXxxNodes is meant
        // for combat-start only — global render/target manager registration
        // happens in a separate combat-init step we can't replicate mid-combat.
        // Disabled. Keeping the method for reference; do not call.
    }

    private static void FullEnemyVisualRebuild(CombatState cs)
    {
        var room = NCombatRoom.Instance;
        if (room == null) { RestoreLogger.Warn("[Rebuild] NCombatRoom.Instance null"); return; }

        // 1. Free every NCreature node in the active + removing lists.
        int freedActive = 0, freedRemoving = 0;
        var activeField = AccessTools.Field(typeof(NCombatRoom), "_creatureNodes");
        var removingField = ReflectionCache.NcrRemovingNodesField;

        if (activeField?.GetValue(room) is System.Collections.IList active)
        {
            foreach (var item in active)
            {
                if (item is not Godot.Node node) continue;
                try { node.GetParent()?.RemoveChild(node); } catch { }
                try { node.Free(); freedActive++; }
                catch { try { node.QueueFree(); } catch { } }
            }
            active.Clear();
        }
        if (removingField?.GetValue(room) is System.Collections.IList removing)
        {
            foreach (var item in removing)
            {
                if (item is not Godot.Node node) continue;
                try { node.GetParent()?.RemoveChild(node); } catch { }
                try { node.Free(); freedRemoving++; }
                catch { try { node.QueueFree(); } catch { } }
            }
            removing.Clear();
        }

        // 2. Sanity-pass on the enemy/ally containers — anything left that
        //    looks like an NCreature gets freed too.
        int orphanFreed = 0;
        var enemyContainerField = AccessTools.Field(typeof(NCombatRoom), "_enemyContainer");
        var allyContainerField = AccessTools.Field(typeof(NCombatRoom), "_allyContainer");
        var ncType = typeof(MegaCrit.Sts2.Core.Nodes.Combat.NCreature);
        foreach (var f in new[] { enemyContainerField, allyContainerField })
        {
            if (f?.GetValue(room) is Godot.Node container)
            {
                foreach (var child in container.GetChildren())
                {
                    if (!ncType.IsInstanceOfType(child)) continue;
                    try { container.RemoveChild(child); child.Free(); orphanFreed++; }
                    catch { try { child.QueueFree(); } catch { } }
                }
            }
        }

        RestoreLogger.Info($"[Rebuild] freed active={freedActive} removing={freedRemoving} orphans={orphanFreed}");

        // 3. Re-instantiate via the same entry points the engine uses on
        //    combat start. These iterate cs.Allies/cs.Enemies and add fresh
        //    NCreature scenes as children of the side containers.
        var createAlly = AccessTools.Method(typeof(NCombatRoom), "CreateAllyNodes");
        var createEnemy = AccessTools.Method(typeof(NCombatRoom), "CreateEnemyNodes");
        try { createAlly?.Invoke(room, null); RestoreLogger.Info("[Rebuild] CreateAllyNodes invoked"); }
        catch (Exception ex) { RestoreLogger.Warn($"[Rebuild] CreateAllyNodes failed: {ex.GetType().Name}:{ex.Message}"); }
        try { createEnemy?.Invoke(room, null); RestoreLogger.Info("[Rebuild] CreateEnemyNodes invoked"); }
        catch (Exception ex) { RestoreLogger.Warn($"[Rebuild] CreateEnemyNodes failed: {ex.GetType().Name}:{ex.Message}"); }

        // 4. Diagnostic dump for the rebuilt creature(s).
        try
        {
            foreach (var c in cs.Creatures)
            {
                if (!c.CombatId.HasValue) continue;
                var fresh = room.GetCreatureNode(c);
                if (fresh != null)
                    DumpReviveDiagnostics(c.CombatId.Value, fresh);
            }
        }
        catch (Exception ex) { RestoreLogger.Warn($"[Rebuild] post-diag failed: {ex.Message}"); }
    }

    /// <summary>
    /// Full revive path. Order matters — CombatManager.AddCreature requires the
    /// creature to ALREADY be in cs._enemies/_allies (per reference impl comment).
    /// </summary>
    private static bool ReviveCreature(CreatureSnapshot saved, CombatState cs)
    {
        var creature = saved.Ref;
        var cm = CombatManager.Instance;
        if (cm == null) return false;

        try
        {
            int beforeCount = cs.Creatures.Count;

            // 1. Re-attach to combat state (set to null on death).
            SetCombatStateOnCreature(creature, cs);

            // 2. HP/MaxHp/Block BEFORE list-add so IsDead==false at the moment
            //    cm.AddCreature inspects the creature.
            int oldHpRev = (int)(ReflectionCache.CreatureHpField.GetValue(creature) ?? 0);
            int oldMaxHpRev = (int)(ReflectionCache.CreatureMaxHpField.GetValue(creature) ?? 0);
            int oldBlockRev = (int)(ReflectionCache.CreatureBlockField.GetValue(creature) ?? 0);
            ReflectionCache.CreatureHpField.SetValue(creature, saved.CurrentHp);
            ReflectionCache.CreatureMaxHpField.SetValue(creature, saved.MaxHp);
            ReflectionCache.CreatureBlockField.SetValue(creature, saved.Block);
            ResetIsDeadIfPresent(creature, saved.IsDead);
            FireDelegateField(creature, ReflectionCache.CreatureCurrentHpChangedField, oldHpRev, saved.CurrentHp);
            FireDelegateField(creature, ReflectionCache.CreatureMaxHpChangedField, oldMaxHpRev, saved.MaxHp);
            FireDelegateField(creature, ReflectionCache.CreatureBlockChangedField, oldBlockRev, saved.Block);
            FireDelegateField(creature, ReflectionCache.CreatureRevivedField, creature);

            // 3. Add to _enemies/_allies FIRST. Reference comment:
            //    "CombatManager.AddCreature requires the creature to already
            //    be in these lists. CombatState.RemoveCreature removed it on death."
            var targetList = creature.Side == CombatSide.Enemy
                ? ReflectionCache.CsEnemiesField.GetValue(cs) as System.Collections.IList
                : ReflectionCache.CsAlliesField.GetValue(cs) as System.Collections.IList;
            if (targetList != null && !targetList.Contains(creature))
                targetList.Add(creature);

            // 4. Null out _moveStateMachine — cm.AddCreature creates a new one
            //    and the setter throws if already set.
            if (creature.Monster != null)
                ReflectionCache.MonsterMoveStateMachineField?.SetValue(creature.Monster, null);

            // 5. Model-layer add — registers with NTargetManager + fires events.
            cm.AddCreature(creature);

            int afterCount = cs.Creatures.Count;
            RestoreLogger.Info($"[Revive] id={saved.CombatId} side={creature.Side} hp={creature.CurrentHp}/{creature.MaxHp} " +
                $"creature.IsDead={creature.IsDead} CombatId.HasValue={creature.CombatId.HasValue} " +
                $"cs.Creatures: {beforeCount}→{afterCount}");

            // 6. Powers (stripped on death).
            RestoreCreaturePowers(creature, saved);

            // 7. Monster RNG + move state.
            if (creature.Monster != null)
            {
                if (saved.MonsterRng is { } ser)
                {
                    ReflectionCache.MonsterRngField?.SetValue(creature.Monster, new Rng(ser));
                }
                if (saved.MonsterMove.HasValue)
                    RestoreMonsterMove(creature.Monster, saved.MonsterMove.Value);
                if (saved.MonsterFields != null)
                    RestoreMonsterFields(creature.Monster, saved.MonsterFields);
            }

            // 8. Visual.
            var room = MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom.Instance;
            if (room != null)
            {
                // Try in-place revive first: find a zombie NCreature in
                // _removingCreatureNodes (death anim left it there). If found,
                // move it to _creatureNodes + StartReviveAnim. This preserves
                // its native MegaSprite/spine bindings — Free + AddCreature
                // path lost those, leaving a stripped 86-descendant shell that
                // doesn't render (verified: zombie_id != new_id, fresh node
                // created mid-combat is incomplete vs combat-start version).
                bool inPlaceRevived = TryInPlaceRevive(room, creature, saved.CombatId);

                // Race detection: TryInPlaceRevive cancels DeathAnimCancelToken
                // before StartReviveAnim, but cts.Cancel() only throws at the
                // next await checkpoint inside AnimDie. If AnimDie was already
                // past the body-cleanup step (a synchronous block between
                // awaits) when cancel arrived, the body is already QueueFree'd
                // and only the UI shell remains. Symptom: NCreatureVisuals._body
                // null, ~half the subtree gone (Visuals/spine/MegaSprite freed),
                // SetUpSkin throws because it expects body to exist.
                //
                // Detection: post-revive zombie's Body property null. If so,
                // the in-place path is unrecoverable — free the zombie entirely
                // and fall through to the AddCreature fresh-instantiation path.
                bool degraded = false;
                if (inPlaceRevived)
                {
                    var probe = room.GetCreatureNode(creature);
                    if (probe == null || IsZombieDegraded(probe))
                    {
                        degraded = true;
                        RestoreLogger.Warn($"[Revive] id={saved.CombatId} in-place succeeded but " +
                            $"body=null after cancel — race lost, recreating fresh");
                    }
                }

                if (!inPlaceRevived || degraded)
                {
                    RestoreLogger.Info($"[Revive] id={saved.CombatId} " +
                        $"{(degraded ? "post-revive degraded" : "no zombie found")}, " +
                        $"falling back to AddCreature path");
                    DestroyZombieNCreatures(room, creature, saved.CombatId);
                    try { room.AddCreature(creature); } catch (Exception vex) { RestoreLogger.Warn($"[Revive] room.AddCreature failed: {vex.Message}"); }
                }
                var node = room.GetCreatureNode(creature);
                if (node != null)
                {
                    ulong newId = 0;
                    try { newId = node.GetInstanceId(); } catch { }
                    RestoreLogger.Info($"[Revive] id={saved.CombatId} new node InstanceId={newId}");

                    TryRestoreBodyFromSavedRef(node, saved);

                    // Untried registration steps. SetCreatureIsInteractable
                    // toggles the input-receive state; UpdateCreatureNavigation
                    // re-wires controller-focus chain; AdjustCreatureScale
                    // re-applies the slot scaling. On combat-start these run
                    // automatically; mid-combat AddCreature might skip them.
                    try
                    {
                        var setInteractable = HarmonyLib.AccessTools.Method(
                            typeof(MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom),
                            "SetCreatureIsInteractable",
                            new[] { typeof(Creature), typeof(bool) });
                        setInteractable?.Invoke(room, new object[] { creature, true });
                        RestoreLogger.Info($"[Revive] SetCreatureIsInteractable(true) invoked");
                    }
                    catch (Exception ex) { RestoreLogger.Warn($"[Revive] SetCreatureIsInteractable failed: {ex.GetType().Name}:{ex.Message}"); }

                    // Direct fallback — call ToggleIsInteractable on the
                    // NCreature node and force-reset hitbox state. The
                    // SetCreatureIsInteractable path above can silently no-op
                    // if NCombatRoom.GetCreatureNode fails to find the just-
                    // revived creature (lookup is by Entity ref through
                    // _creatureNodes; if our IList add via reflection landed
                    // out of the list type-checked path, the LINQ FirstOrDefault
                    // wouldn't see it). Also restore Hitbox.FocusMode that
                    // StartDeathAnim flipped to None — StartReviveAnim resets
                    // MouseFilter but not FocusMode, blocking controller/keyboard
                    // navigation onto the revived creature.
                    try
                    {
                        node.ToggleIsInteractable(node.Entity?.Monster?.IsHealthBarVisible ?? true);
                        if (node.Hitbox != null)
                        {
                            node.Hitbox.MouseFilter = Godot.Control.MouseFilterEnum.Stop;
                            node.Hitbox.FocusMode = Godot.Control.FocusModeEnum.All;
                            node.Hitbox.Visible = true;
                        }

                        // Force-clear the hover-stuck state. If the mouse was
                        // hovering the creature at the moment of death (common
                        // case — player just played an attack card, mouse
                        // still over the target), MouseEntered fired OnFocus →
                        // IsFocused = true. AnimDiePatch detaches NCreature
                        // before MouseExited can fire → IsFocused stays true.
                        // After revive, the next MouseEntered → OnFocus sees
                        // IsFocused == true and returns immediately, skipping
                        // NTargetManager.OnNodeHovered. Single-target hover
                        // never registers; AOE ignores hover and works fine.
                        //
                        // Try multiple field names to handle name-mangling
                        // variations across compilers/.NET versions.
                        bool resetSomething = false;
                        foreach (var fieldName in new[] {
                            "<IsFocused>k__BackingField", "_isFocused", "isFocused" })
                        {
                            var f = HarmonyLib.AccessTools.Field(
                                typeof(MegaCrit.Sts2.Core.Nodes.Combat.NCreature),
                                fieldName);
                            if (f != null)
                            {
                                f.SetValue(node, false);
                                RestoreLogger.Info($"[Revive] reset {fieldName}=false");
                                resetSomething = true;
                            }
                        }
                        if (!resetSomething)
                            RestoreLogger.Warn("[Revive] could not find IsFocused backing field on NCreature");

                        // OnUnfocus reflection call removed 2026-04-28: was
                        // causing stuck hover-tip on the rightmost enemy.
                        // OnUnfocus invokes NTargetManager.OnNodeUnhovered(this)
                        // which mutates NTargetManager.HoveredNode global state,
                        // and HideHoverTips/HideNameplate side-effect across
                        // creature visuals. With the IsFocused reset above
                        // already in place, OnUnfocus was redundant cleanup
                        // that did more harm than good.

                        RestoreLogger.Info($"[Revive] direct ToggleIsInteractable+Hitbox reset done");
                    }
                    catch (Exception ex) { RestoreLogger.Warn($"[Revive] direct hitbox restore failed: {ex.Message}"); }

                    // UpdateBounds + HITBOX-DIAG + signal reconnect blocks
                    // removed 2026-04-28 after probe.log diagnostics confirmed
                    // hitbox state was always correct out of the in-place
                    // revive path (mf=Stop, fm=All, mEntConns=1, size/pos
                    // sane). The aggressive UpdateBounds invocation may have
                    // contributed to the stuck-nameplate-on-rightmost-enemy
                    // issue by triggering NCreatureStateDisplay.SetCreatureBounds
                    // side-effects on a creature whose state-display was
                    // already in a hovered state. Keep the IsFocused reset
                    // (the actual targeting fix) above; drop the rest.

                    try
                    {
                        var updateNav = HarmonyLib.AccessTools.Method(
                            typeof(MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom), "UpdateCreatureNavigation");
                        updateNav?.Invoke(room, null);
                        RestoreLogger.Info($"[Revive] UpdateCreatureNavigation invoked");
                    }
                    catch (Exception ex) { RestoreLogger.Warn($"[Revive] UpdateCreatureNavigation failed: {ex.GetType().Name}:{ex.Message}"); }

                    try
                    {
                        var adjust = HarmonyLib.AccessTools.Method(
                            typeof(MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom),
                            "AdjustCreatureScaleForAspectRatio");
                        adjust?.Invoke(room, null);
                        RestoreLogger.Info($"[Revive] AdjustCreatureScaleForAspectRatio invoked");
                    }
                    catch (Exception ex) { RestoreLogger.Warn($"[Revive] AdjustCreatureScaleForAspectRatio failed: {ex.GetType().Name}:{ex.Message}"); }

                    // After room.AddCreature, NCreature exists with most subtree
                    // but the actual creature sprite (Sprite2D), intent display
                    // (NIntent + MegaRichTextLabel), and idle particles are
                    // missing — they're lazy-initialized via NCreatureVisuals.
                    // Direct entry point is SetUpSkin(MonsterModel) which rebuilds
                    // the skin and triggers attached node creation.
                    //
                    // CRITICAL: only run SetUpSkin/_Ready for the AddCreature
                    // fallback path. For successful in-place revive, the zombie
                    // already has its body fully set up from before death —
                    // calling SetUpSkin on an already-initialized
                    // NCreatureVisuals frees the live _body, creates a fresh
                    // MegaSprite that's NOT attached to the tree, and leaves
                    // the body subtree entirely missing (~48 Node2Ds). The user
                    // sees an invisible enemy with intact UI shell only.
                    bool runSkinInit = !inPlaceRevived || degraded;
                    if (creature.Monster != null && runSkinInit)
                    {
                        foreach (var n in WalkNodeTree(node))
                        {
                            if (ReflectionCache.NCreatureVisualsType?.IsInstanceOfType(n) != true) continue;
                            try
                            {
                                var setUpSkin = HarmonyLib.AccessTools.Method(
                                    ReflectionCache.NCreatureVisualsType!, "SetUpSkin",
                                    new[] { typeof(MegaCrit.Sts2.Core.Models.MonsterModel) });
                                if (setUpSkin != null)
                                {
                                    setUpSkin.Invoke(n, new object[] { creature.Monster });
                                    RestoreLogger.Info($"[Revive] SetUpSkin(MonsterModel) invoked on NCreatureVisuals");
                                }
                                else RestoreLogger.Warn("[Revive] SetUpSkin method not resolved");
                            }
                            catch (Exception ex) { RestoreLogger.Warn($"[Revive] SetUpSkin failed: {ex.GetType().Name}:{ex.Message}"); }

                            // After SetUpSkin, also force-call _Ready on
                            // NCreatureVisuals — Godot's lazy-init hook that
                            // builds children in tree. AddCreature may have
                            // reused a zombie shell whose _Ready already ran;
                            // without re-running, Sprite2D / NIntent /
                            // CpuParticles2D children stay missing.
                            try
                            {
                                var ready = HarmonyLib.AccessTools.Method(n.GetType(), "_Ready");
                                if (ready != null)
                                {
                                    ready.Invoke(n, null);
                                    RestoreLogger.Info($"[Revive] NCreatureVisuals._Ready() invoked");
                                }
                            }
                            catch (Exception ex) { RestoreLogger.Warn($"[Revive] NCreatureVisuals._Ready failed: {ex.GetType().Name}:{ex.Message}"); }
                        }
                    }
                    else if (creature.Monster != null && !runSkinInit)
                    {
                        RestoreLogger.Info($"[Revive] id={saved.CombatId} skipping SetUpSkin/_Ready " +
                            $"(in-place revive — body already initialized)");
                    }

                    // Reparent the NCreatureVisuals subtree back under NCreature.
                    // AnimDie can move it to a death-effect overlay (or otherwise
                    // detach it from NCreature) — after revive the field
                    // _Visuals_ still points to a live in-tree node, but it's
                    // no longer a child of NCreature. Result: body and its 22
                    // children (spine bones, slots) aren't reachable when the
                    // engine renders NCreature, so the creature is invisible
                    // even though every individual node is "alive".
                    TryReparentVisualsUnderCreature(node, saved.CombatId);

                    // Re-attach body and SpineBody if they were detached by
                    // AnimDiePatch. Body's natural parent is NCreatureVisuals;
                    // SpineBody's natural parent is body. If either was
                    // detached on death, re-AddChild puts them back so the
                    // creature renders again.
                    TryReattachDeathDetachedNodes(node, saved.CombatId);

                    if (saved.HadVisualNode)
                    {
                        try { node.GlobalPosition = saved.VisualPosition; } catch { }
                        try
                        {
                            var body = node.Body;
                            if (body != null)
                            {
                                body.Scale = saved.VisualBodyScale;
                                // body.Position / Rotation: AnimDie tweens these
                                // during death anim (slide/collapse). Without
                                // restoring, revived body stays at mid-death
                                // offset and renders off-screen.
                                body.Position = saved.VisualBodyPosition;
                                body.Rotation = saved.VisualBodyRotation;
                                // Force-show. AnimDiePatch hid body via
                                // Visible=false when the creature died.
                                body.Visible = true;
                                if (body is Godot.CanvasItem bodyCi)
                                    bodyCi.Modulate = saved.VisualBodyModulate;
                            }
                            if (node.Visuals is Godot.Node2D visualsN2D)
                                visualsN2D.Visible = true;
                        }
                        catch { }
                    }
                    // Don't call StartReviveAnim here. For in-place revive it
                    // was already invoked inside TryInPlaceRevive on the actual
                    // zombie node. For fresh AddCreature, the creature is
                    // newly instantiated as alive — calling StartReviveAnim
                    // schedules an async revive flow on a not-actually-dead
                    // creature, which hundreds of ms later disposes _body
                    // (verified: log shows body alive immediately after revive,
                    // then "Cannot access a disposed object" on next refresh).
                    ProbeAndFixVisibility(node, saved.CombatId);
                    DumpReviveDiagnostics(saved.CombatId, node);
                }
                else
                {
                    RestoreLogger.Warn($"[Revive] no visual node found after AddCreature for id={saved.CombatId}");
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            RestoreLogger.Warn($"[Revive] id={saved.CombatId} failed: {ex.Message}\n{ex.StackTrace}");
            return false;
        }
    }

    /// <summary>
    /// If the live NCreatureVisuals has lost its _body Node2D (death anim Free'd
    /// it from the zombie subtree → in-place revive comes back with no body),
    /// check if our snapshot-time strong ref is still IsInstanceValid. If yes,
    /// re-attach. This handles the late-Z case where user pressed Z after the
    /// die anim ran the body-free step.
    /// </summary>
    internal static void TryRestoreBodyFromSavedRef(
        MegaCrit.Sts2.Core.Nodes.Combat.NCreature node, CreatureSnapshot saved)
    {
        try
        {
            var visuals = node.Visuals;
            if (visuals == null) { RestoreLogger.Info($"[BodyRestore] id={saved.CombatId} live NCreatureVisuals null — skip"); return; }

            var bodyField = HarmonyLib.AccessTools.Field(visuals.GetType(), "_body");
            var liveBody = bodyField?.GetValue(visuals) as Godot.Node;

            bool liveOk = liveBody != null && Godot.GodotObject.IsInstanceValid(liveBody);
            try { if (liveOk) liveOk = liveBody!.IsInsideTree(); } catch { liveOk = false; }

            // Body presence is necessary but not sufficient. NMonsterDeathVfx.PlayVfx
            // REPARENTS the body into a SubViewport inside the VFX node — body is
            // still IsInstanceValid + IsInsideTree, but its parent is the VFX,
            // not NCreatureVisuals. When the VFX QueueFrees, the body goes with it.
            // If we leave it there, the user sees the creature disappear after vfx
            // completes despite a successful undo. Force-pull the body back when
            // the parent isn't NCreatureVisuals.
            bool parentOk = false;
            if (liveOk)
            {
                try
                {
                    var bodyParent = liveBody!.GetParent();
                    parentOk = ReferenceEquals(bodyParent, visuals);
                    if (!parentOk)
                        RestoreLogger.Info($"[BodyRestore] id={saved.CombatId} body parent='{bodyParent?.Name}' (type {bodyParent?.GetType().Name}), expected NCreatureVisuals — will reparent");
                }
                catch { parentOk = false; }
            }

            if (liveOk && parentOk)
            {
                RestoreLogger.Info($"[BodyRestore] id={saved.CombatId} live body OK, no restore needed");
                return;
            }

            // Body alive but parented to VFX subtree → reparent it back.
            if (liveOk && !parentOk)
            {
                try
                {
                    var oldParent = liveBody!.GetParent();
                    if (oldParent != null) oldParent.RemoveChild(liveBody);
                    if (visuals is Godot.Node visualsNode2)
                    {
                        visualsNode2.AddChild(liveBody);
                        bodyField?.SetValue(visuals, liveBody);

                        // NMonsterDeathVfx.PlayVfx had set body.Position to a
                        // viewport-local value (`globalPosition - vector10`),
                        // i.e. an absolute screen offset. After reparent, that
                        // value is interpreted in NCreatureVisuals' local space
                        // and the sprite renders far off-screen / in odd
                        // positions. Reset transforms from the snapshot.
                        if (liveBody is Godot.Node2D body2d)
                        {
                            try { body2d.Position = saved.VisualBodyPosition; } catch { }
                            try { body2d.Scale    = saved.VisualBodyScale;    } catch { }
                            try { body2d.Rotation = saved.VisualBodyRotation; } catch { }
                            try { body2d.Visible  = true; } catch { }
                            try
                            {
                                if (body2d is Godot.CanvasItem bodyCi)
                                    bodyCi.Modulate = saved.VisualBodyModulate;
                            }
                            catch { }
                        }
                        RestoreLogger.Info($"[BodyRestore] id={saved.CombatId} reparented body from VFX subtree back to NCreatureVisuals (transforms reset)");
                    }
                    return;
                }
                catch (Exception ex)
                { RestoreLogger.Warn($"[BodyRestore] vfx-reparent rescue failed: {ex.Message}"); }
            }

            if (saved.BodyRef == null)
            {
                RestoreLogger.Warn($"[BodyRestore] id={saved.CombatId} live body missing AND no saved ref — body will be invisible");
                return;
            }

            bool savedValid = Godot.GodotObject.IsInstanceValid(saved.BodyRef);
            RestoreLogger.Info($"[BodyRestore] id={saved.CombatId} live body missing; savedRef.IsInstanceValid={savedValid}");
            if (!savedValid) return;

            // Detach saved body from its current parent (if attached anywhere).
            try
            {
                var curParent = saved.BodyRef.GetParent();
                if (curParent != null) curParent.RemoveChild(saved.BodyRef);
            }
            catch (Exception ex) { RestoreLogger.Warn($"[BodyRestore] detach old parent failed: {ex.Message}"); }

            // Re-attach as child of live NCreatureVisuals (which is the body's
            // intended parent — saved.BodyParentRef would have pointed to the
            // old NCreatureVisuals which may itself be freed; safer to use the
            // live one).
            try
            {
                if (visuals is Godot.Node visualsNode)
                {
                    visualsNode.AddChild(saved.BodyRef);
                    bodyField?.SetValue(visuals, saved.BodyRef);
                    RestoreLogger.Info($"[BodyRestore] id={saved.CombatId} re-attached saved body to live NCreatureVisuals");
                }
            }
            catch (Exception ex) { RestoreLogger.Warn($"[BodyRestore] re-attach failed: {ex.GetType().Name}:{ex.Message}"); }
        }
        catch (Exception ex) { RestoreLogger.Warn($"[BodyRestore] id={saved.CombatId} failed: {ex.Message}"); }
    }

    /// <summary>
    /// Move a zombie NCreature from _removingCreatureNodes to _creatureNodes
    /// (and out of EnemyContainer-as-removing into EnemyContainer-as-active if
    /// it's the same node, which it usually is — the death anim doesn't
    /// reparent, just lists). Then StartReviveAnim. This preserves the
    /// native MegaSprite/spine bindings from death state, which Free destroys
    /// and AddCreature can't recreate mid-combat.
    /// </summary>
    private static bool TryInPlaceRevive(
        MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom room,
        Creature creature,
        uint combatId)
    {
        try
        {
            // Zombie search order:
            //  1. AnimDiePatch.DetachedZombies — our own registry of NCreatures
            //     we removed from EnemyContainer when AnimDie's replacement
            //     ran. Tree-walk and removing-list searches won't find these
            //     because they're orphaned (held only by our static dict).
            //  2. _removingCreatureNodes — game's normal post-death-anim list.
            //  3. Enemy/AllyContainer scene subtree — last-resort walk.
            MegaCrit.Sts2.Core.Nodes.Combat.NCreature? zombie = null;
            if (Patches.AnimDiePatch.DetachedZombies.TryGetValue(creature, out var detached)
                && detached != null && Godot.GodotObject.IsInstanceValid(detached))
            {
                zombie = detached;
                RestoreLogger.Info($"[Revive] id={combatId} in-place: zombie found in DetachedZombies registry");
            }

            if (zombie == null && ReflectionCache.NcrRemovingNodesField?.GetValue(room)
                is System.Collections.IList removingList)
            {
                foreach (var item in removingList)
                {
                    if (item is not MegaCrit.Sts2.Core.Nodes.Combat.NCreature nc) continue;
                    var ent = ReflectionCache.NCreatureEntityProp?.GetValue(nc) as Creature;
                    if (ReferenceEquals(ent, creature)) { zombie = nc; break; }
                }
            }

            if (zombie == null)
            {
                foreach (var nc in EnumerateAllNCreatureNodes(room))
                {
                    var ent = ReflectionCache.NCreatureEntityProp?.GetValue(nc) as Creature;
                    if (ReferenceEquals(ent, creature)) { zombie = nc; break; }
                }
            }

            if (zombie == null)
            {
                RestoreLogger.Info($"[Revive] id={combatId} in-place: no zombie found");
                return false;
            }

            ulong zid = 0;
            try { zid = zombie.GetInstanceId(); } catch { }
            RestoreLogger.Info($"[Revive] id={combatId} in-place: zombie InstanceId={zid}");

            // 1. Remove from _removingCreatureNodes.
            if (ReflectionCache.NcrRemovingNodesField?.GetValue(room)
                is System.Collections.IList removingMutable)
            {
                while (removingMutable.Contains(zombie)) removingMutable.Remove(zombie);
            }

            // 2. Add to _creatureNodes (active list).
            var activeField = HarmonyLib.AccessTools.Field(
                typeof(MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom), "_creatureNodes");
            if (activeField?.GetValue(room) is System.Collections.IList activeList)
            {
                if (!activeList.Contains(zombie)) activeList.Add(zombie);
            }

            // 3. Ensure it's still parented under EnemyContainer/AllyContainer.
            //    If the death anim's QueueFree was about to detach it, force
            //    re-parent. (Usually no-op — death anim lists, not reparents.)
            try
            {
                if (zombie.GetParent() == null)
                {
                    var containerField = creature.Side == CombatSide.Enemy
                        ? HarmonyLib.AccessTools.Field(typeof(MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom), "_enemyContainer")
                        : HarmonyLib.AccessTools.Field(typeof(MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom), "_allyContainer");
                    if (containerField?.GetValue(room) is Godot.Node container)
                    {
                        container.AddChild(zombie);
                        RestoreLogger.Info($"[Revive] id={combatId} in-place: reparented to {container.Name}");
                    }
                }
            }
            catch (Exception ex) { RestoreLogger.Warn($"[Revive] in-place reparent failed: {ex.Message}"); }

            // 4. Cancel the death-anim cancellation token. With AnimDie now
            //    replaced by AnimDiePatch (which doesn't free body), cancel
            //    is safe — at worst it shortens our replacement's wait. We
            //    still want to interrupt any in-flight wait so the alpha=0
            //    step doesn't fire after revive, leaving the just-revived
            //    creature briefly invisible. Also kills tweens defensively.
            CancelDeathAnimAndKillTweens(zombie, combatId);

            // 4b. Kill our own AnimDie fade-out tween if still running. This
            //     tween animates `modulate:a → 0` and survives the death-anim
            //     cancel token because it's a Godot Tween, not a Task. Without
            //     killing it, the alpha keeps lerping toward 0 after revive
            //     and the creature looks transparent (the floor showing
            //     through, easily mistaken for a stuck overlay graphic).
            try
            {
                if (Patches.AnimDiePatch.ActiveFadeTweens.TryGetValue(zombie, out var fadeTw))
                {
                    if (fadeTw != null && Godot.GodotObject.IsInstanceValid(fadeTw) && fadeTw.IsValid())
                    {
                        fadeTw.Kill();
                        RestoreLogger.Info($"[Revive] id={combatId} killed in-flight fade-out tween");
                    }
                    Patches.AnimDiePatch.ActiveFadeTweens.Remove(zombie);
                }
                // Force creature.Modulate.A back to 1 so any tween write that
                // already landed this frame is overridden.
                var m = zombie.Modulate;
                zombie.Modulate = new Godot.Color(m.R, m.G, m.B, 1f);

                // Body modulate: the fade tween (re-enabled 2026-04-28)
                // targets body.Modulate.A specifically, not the NCreature's
                // own Modulate. Reset body alpha back to 1 here so revive
                // visually pops in clean even if the tween's Kill() raced
                // ahead but the modulate was already mid-fade.
                try
                {
                    if (zombie.Body is Godot.CanvasItem bodyCi)
                    {
                        var bm = bodyCi.Modulate;
                        bodyCi.Modulate = new Godot.Color(bm.R, bm.G, bm.B, 1f);
                    }
                }
                catch { }
            }
            catch (Exception ex) { RestoreLogger.Warn($"[Revive] fade-tween kill: {ex.Message}"); }

            // 4c. Refresh NSelectionReticle._cancelToken. The reticle's
            //     _ExitTree fired its own CancellationTokenSource.Cancel()
            //     when AnimDie detached this NCreature; on re-attach
            //     (`AddChild`) Godot doesn't re-run _Ready/_EnterTree to
            //     rebuild the token. After revive, OnSelect still works
            //     (no cancel check), but OnDeselect short-circuits at
            //     `if (!_cancelToken.IsCancellationRequested)` so the
            //     targeting reticle stays at modulate=White / IsSelected=true
            //     after the next card play — a stuck outline around the enemy.
            //     Replace the field with a fresh CTS via reflection.
            ResetReticleCancelTokens(zombie, combatId);

            // 5. StartReviveAnim — game's built-in revive entry. With our
            //    AnimDie replacement, body has not been freed, so this can
            //    run safely without triggering disposal-cascade behavior.
            try { zombie.StartReviveAnim(); RestoreLogger.Info($"[Revive] id={combatId} in-place: StartReviveAnim ok"); }
            catch (Exception ex) { RestoreLogger.Warn($"[Revive] in-place StartReviveAnim failed: {ex.Message}"); }

            // Clear our registry entry — zombie is now the live creature
            // again. Future deaths will re-register a fresh entry.
            try { Patches.AnimDiePatch.DetachedZombies.Remove(creature); } catch { }

            return true;
        }
        catch (Exception ex)
        {
            RestoreLogger.Warn($"[Revive] id={combatId} in-place failed: {ex.GetType().Name}:{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Re-attach body and SpineBody nodes that AnimDiePatch detached when the
    /// creature died. The detach makes the dead creature actually invisible
    /// (the only reliable way given MegaSprite's custom render path); revive
    /// reverses by AddChild back to the natural parent.
    /// - body's natural parent: NCreatureVisuals (held by Visuals property)
    /// - SpineBody's natural parent: body (the Visuals Node2D)
    /// </summary>
    private static void TryReattachDeathDetachedNodes(
        MegaCrit.Sts2.Core.Nodes.Combat.NCreature node, uint combatId)
    {
        try
        {
            object? visuals = null;
            try { visuals = node.Visuals; } catch { }
            if (visuals is not Godot.Node visualsNode)
            {
                RestoreLogger.Info($"[Reattach] id={combatId} Visuals not a Node — skip");
                return;
            }

            // Re-attach body to NCreatureVisuals if detached.
            try
            {
                var body = node.Body;
                if (body != null && Godot.GodotObject.IsInstanceValid(body))
                {
                    if (body.GetParent() == null)
                    {
                        visualsNode.AddChild(body);
                        RestoreLogger.Info($"[Reattach] id={combatId} body re-attached to Visuals");
                    }
                }
            }
            catch (Exception ex) { RestoreLogger.Warn($"[Reattach] id={combatId} body re-attach: {ex.Message}"); }

            // Re-attach SpineBody to body if detached.
            try
            {
                var visualsType = ReflectionCache.NCreatureVisualsType;
                if (visualsType != null)
                {
                    var spineBodyProp = HarmonyLib.AccessTools.Property(visualsType, "SpineBody");
                    var spineBody = spineBodyProp?.GetValue(visuals);
                    if (spineBody is Godot.Node spineN && Godot.GodotObject.IsInstanceValid(spineN))
                    {
                        if (spineN.GetParent() == null)
                        {
                            var body = node.Body;
                            if (body != null) body.AddChild(spineN);
                            else visualsNode.AddChild(spineN);
                            RestoreLogger.Info($"[Reattach] id={combatId} SpineBody re-attached");
                        }
                        if (spineBody is Godot.CanvasItem spineCi)
                            spineCi.Visible = true;
                    }
                }
            }
            catch (Exception ex) { RestoreLogger.Warn($"[Reattach] id={combatId} SpineBody: {ex.Message}"); }
        }
        catch (Exception ex)
        {
            RestoreLogger.Warn($"[Reattach] id={combatId} failed: {ex.GetType().Name}:{ex.Message}");
        }
    }

    /// <summary>
    /// Verify NCreatureVisuals (the 'Toadpole'-style child holding the body
    /// + spine subtree) is parented under the given NCreature. AnimDie can
    /// reparent the visuals to a death effect overlay during the death
    /// sequence; after revive the visuals' field reference is still valid
    /// and the node is in the SceneTree, but it's no longer a child of
    /// NCreature, so the body subtree (~23 nodes including spine bones) is
    /// not rendered as part of the creature.
    /// </summary>
    private static void TryReparentVisualsUnderCreature(
        MegaCrit.Sts2.Core.Nodes.Combat.NCreature node, uint combatId)
    {
        try
        {
            object? visualsObj = null;
            try { visualsObj = node.Visuals; } catch { }
            if (visualsObj is not Godot.Node visualsNode)
            {
                RestoreLogger.Info($"[Reparent] id={combatId} Visuals is not a Node — skip");
                return;
            }
            if (!Godot.GodotObject.IsInstanceValid(visualsNode))
            {
                RestoreLogger.Warn($"[Reparent] id={combatId} Visuals is invalid (freed)");
                return;
            }

            Godot.Node? currentParent = null;
            try { currentParent = visualsNode.GetParent(); } catch { }
            if (ReferenceEquals(currentParent, node))
            {
                RestoreLogger.Info($"[Reparent] id={combatId} Visuals already under NCreature — skip");
                return;
            }

            string oldParentDesc = "<null>";
            if (currentParent != null)
            {
                try { oldParentDesc = $"{currentParent.GetType().Name}:'{currentParent.Name}'"; }
                catch { oldParentDesc = currentParent.GetType().Name; }
            }
            RestoreLogger.Info($"[Reparent] id={combatId} moving Visuals from {oldParentDesc} → NCreature");

            // Capture the global transform so reparenting doesn't snap the
            // visuals to a different on-screen position. After RemoveChild +
            // AddChild, the local position is unchanged but the new parent's
            // transform applies — so we restore global pose explicitly. The
            // body.Position restore that runs after this still wins for the
            // body's offset within Visuals.
            Godot.Vector2 globalPosBefore = Godot.Vector2.Zero;
            float globalRotBefore = 0f;
            Godot.Vector2 globalScaleBefore = Godot.Vector2.One;
            if (visualsNode is Godot.Node2D vn2dBefore)
            {
                try { globalPosBefore = vn2dBefore.GlobalPosition; } catch { }
                try { globalRotBefore = vn2dBefore.GlobalRotation; } catch { }
                try { globalScaleBefore = vn2dBefore.GlobalScale; } catch { }
            }

            try { currentParent?.RemoveChild(visualsNode); }
            catch (Exception ex) { RestoreLogger.Warn($"[Reparent] id={combatId} RemoveChild failed: {ex.Message}"); }

            try { node.AddChild(visualsNode); }
            catch (Exception ex)
            {
                RestoreLogger.Warn($"[Reparent] id={combatId} AddChild failed: {ex.Message}");
                return;
            }

            // Restore global transform. Local will resolve based on NCreature's
            // transform after this write.
            if (visualsNode is Godot.Node2D vn2dAfter)
            {
                try { vn2dAfter.GlobalPosition = globalPosBefore; } catch { }
                try { vn2dAfter.GlobalRotation = globalRotBefore; } catch { }
                try { vn2dAfter.GlobalScale = globalScaleBefore; } catch { }
            }

            RestoreLogger.Info($"[Reparent] id={combatId} reparent complete");
        }
        catch (Exception ex)
        {
            RestoreLogger.Warn($"[Reparent] id={combatId} failed: {ex.GetType().Name}:{ex.Message}");
        }
    }

    /// <summary>
    /// Cancel the in-flight death-animation async Task and kill the visual
    /// Tweens that drive it. Must run BEFORE StartReviveAnim so the revive
    /// has a clean slate: the cancellation token throws at the next await
    /// inside AnimDie, the tweens stop animating modulate/scale, and the
    /// post-death cleanup step (RemoveCreatureNode/QueueFree) never fires.
    /// </summary>
    private static void CancelDeathAnimAndKillTweens(
        MegaCrit.Sts2.Core.Nodes.Combat.NCreature zombie, uint combatId)
    {
        // Step 1: cancel the AnimDie async task. The token is on a property
        // (DeathAnimCancelToken). After Cancel(), the next `await ToSignal(...)`
        // or token.ThrowIfCancellationRequested() inside AnimDie will throw
        // OperationCanceledException, unwinding the task without completing
        // the post-death cleanup steps.
        try
        {
            bool wasPlaying = false;
            try
            {
                if (ReflectionCache.NCreatureIsPlayingDeathAnimProp?.GetValue(zombie) is bool b)
                    wasPlaying = b;
            }
            catch { }

            var cts = ReflectionCache.NCreatureDeathAnimCancelTokenProp?.GetValue(zombie)
                as System.Threading.CancellationTokenSource;
            if (cts != null)
            {
                bool alreadyCancelled = false;
                try { alreadyCancelled = cts.IsCancellationRequested; } catch { }
                if (!alreadyCancelled)
                {
                    try { cts.Cancel(); }
                    catch (Exception ex) { RestoreLogger.Warn($"[Revive] id={combatId} cts.Cancel: {ex.Message}"); }
                }
                RestoreLogger.Info($"[Revive] id={combatId} death-anim cancel: " +
                    $"wasPlaying={wasPlaying} cts={(cts == null ? "null" : "ok")} alreadyCancelled={alreadyCancelled}");
            }
            else
            {
                RestoreLogger.Info($"[Revive] id={combatId} death-anim cancel: cts=null (wasPlaying={wasPlaying})");
            }
        }
        catch (Exception ex) { RestoreLogger.Warn($"[Revive] id={combatId} death cancel failed: {ex.Message}"); }

        // Step 2: kill the Tweens that AnimDie was driving. _intentFadeTween
        // fades the intent display, _shakeTween shakes on hit-killed, _scaleTween
        // animates death scale shrink. If any are still alive after token
        // cancel, they'll keep tweening their target props for one more frame.
        // Kill them so the property writes stop immediately.
        TryKillTween(zombie, ReflectionCache.NCreatureIntentFadeTweenField, "_intentFadeTween", combatId);
        TryKillTween(zombie, ReflectionCache.NCreatureShakeTweenField, "_shakeTween", combatId);
        TryKillTween(zombie, ReflectionCache.NCreatureScaleTweenField, "_scaleTween", combatId);
    }

    private static void TryKillTween(object owner, FieldInfo? field, string label, uint combatId)
    {
        if (field == null) return;
        try
        {
            if (field.GetValue(owner) is Godot.Tween t && t.IsValid())
            {
                t.Kill();
                RestoreLogger.Info($"[Revive] id={combatId} killed {label}");
            }
        }
        catch (Exception ex) { RestoreLogger.Warn($"[Revive] id={combatId} kill {label}: {ex.Message}"); }
    }

    /// <summary>
    /// Check whether an in-place-revived NCreature has lost its body to the
    /// AnimDie race. We detect by inspecting NCreatureVisuals._body — if null,
    /// AnimDie's body-cleanup step ran before our cancel propagated. The
    /// MegaSprite (SpineBody) is also checked — IsInstanceValid catches the
    /// case where the field still holds a stale reference to a freed Node.
    /// </summary>
    internal static bool IsZombieDegraded(MegaCrit.Sts2.Core.Nodes.Combat.NCreature node)
    {
        try
        {
            // node.Body delegates to NCreatureVisuals._body, but accessing it
            // through the property can throw if Visuals is also gone. Probe
            // both layers defensively.
            object? visuals = null;
            try { visuals = node.Visuals; } catch { return true; }
            if (visuals == null) return true;

            var visualsType = ReflectionCache.NCreatureVisualsType;
            if (visualsType != null)
            {
                var bodyField = HarmonyLib.AccessTools.Field(visualsType, "_body");
                if (bodyField != null)
                {
                    var body = bodyField.GetValue(visuals) as Godot.Node;
                    if (body == null) return true;
                    if (!Godot.GodotObject.IsInstanceValid(body)) return true;
                }

                // SpineBody (MegaSprite) — the actual spine renderer. If this
                // is null/invalid, the spine subtree is gone even if _body is
                // still attached.
                var spineBodyProp = HarmonyLib.AccessTools.Property(visualsType, "SpineBody");
                if (spineBodyProp != null)
                {
                    object? spineBody;
                    try { spineBody = spineBodyProp.GetValue(visuals); }
                    catch { return true; }
                    if (spineBody == null) return true;
                    if (spineBody is Godot.GodotObject go && !Godot.GodotObject.IsInstanceValid(go)) return true;
                }
            }
        }
        catch
        {
            // Any reflection error during the check → assume degraded so the
            // caller falls back to the safer AddCreature path.
            return true;
        }
        return false;
    }

    /// <summary>
    /// Diagnostic probe — DOES NOT force-fix anymore. Earlier the function
    /// flipped Visible=true and Modulate.A=1 on every CanvasItem found below
    /// threshold, but that revealed nodes that were *meant* to be hidden:
    /// NHealthBar's 9-slice background panels (DoomForeground, PoisonForeground,
    /// HpMiddleground, BlockOutline, InfinityTex), BlockContainer, etc. — all
    /// shown only when their condition (poison stacks, doom, block) is active.
    /// Forcing them visible plastered a striped/9-slice graphic over the
    /// creature sprite (the user-reported "stuck overlay" bug, 2026-04-27).
    /// Keep the walk for diagnostics; let the game manage visibility itself.
    /// In-place revive preserves the original NCreature with its visibility
    /// state intact, so post-revive the body should already be visible
    /// without any forcing.
    /// </summary>
    private static void ProbeAndFixVisibility(Godot.Node root, uint combatId)
    {
        try
        {
            bool valid = Godot.GodotObject.IsInstanceValid(root);
            string parentName = "?";
            int siblingIdx = -1;
            int siblingCount = -1;
            try
            {
                var parent = root.GetParent();
                parentName = parent?.Name ?? "<orphan>";
                if (parent != null)
                {
                    var sibs = parent.GetChildren();
                    siblingCount = sibs.Count;
                    for (int i = 0; i < sibs.Count; i++)
                        if (ReferenceEquals(sibs[i], root)) { siblingIdx = i; break; }
                }
            }
            catch { }
            RestoreLogger.Info($"[VisFix] id={combatId} valid={valid} parent={parentName} siblingIdx={siblingIdx}/{siblingCount}");
        }
        catch { }

        int totalCi = 0, hidden = 0, lowAlphaMod = 0, lowAlphaSelfMod = 0, playingAnim = 0;
        var hiddenList = new List<string>();
        var animList = new List<string>();

        foreach (var n in WalkNodeTree(root))
        {
            try
            {
                if (n is Godot.CanvasItem ci)
                {
                    totalCi++;
                    if (!ci.Visible)
                    {
                        hidden++;
                        if (hiddenList.Count < 20) hiddenList.Add($"{n.GetType().Name}:{n.Name}");
                        // Removed: ci.Visible = true. Hidden CanvasItems are
                        // overwhelmingly intentional (HP bar foregrounds when
                        // condition not active, etc.).
                    }
                    var m = ci.Modulate;
                    if (m.A < 0.99f) lowAlphaMod++;
                    var sm = ci.SelfModulate;
                    if (sm.A < 0.99f) lowAlphaSelfMod++;
                }
                if (n is Godot.AnimationPlayer ap)
                {
                    if (ap.IsPlaying())
                    {
                        playingAnim++;
                        if (animList.Count < 10)
                            animList.Add($"{n.Name}:'{ap.CurrentAnimation}'@{ap.CurrentAnimationPosition:F2}/{ap.CurrentAnimationLength:F2}");
                        // Removed: ap.Stop(keepState: false). Game's own
                        // AnimationPlayers — stopping them caused regressions
                        // similar to the visibility-force issue.
                    }
                }
            }
            catch { }
        }

        RestoreLogger.Info($"[VisFix] id={combatId} canvasItems={totalCi} hidden={hidden} lowMod={lowAlphaMod} lowSelfMod={lowAlphaSelfMod} animPlaying={playingAnim}");
        if (hiddenList.Count > 0)
            RestoreLogger.Info($"[VisFix] id={combatId} hidden nodes: {string.Join(", ", hiddenList)}");
        if (animList.Count > 0)
            RestoreLogger.Info($"[VisFix] id={combatId} active anims: {string.Join(", ", animList)}");
    }

    /// <summary>
    /// Force-invoke `_Ready()` on a Godot.Node via reflection. Used to re-trigger
    /// lazy children build on a re-attached node. The method is protected on
    /// Godot.Node and we want to call it on the *current* class (e.g. NCreature)
    /// so that overrides run, hence DeclaringType walk.
    /// </summary>
    private static void TryReReady(Godot.Node n, string label)
    {
        try
        {
            var ready = HarmonyLib.AccessTools.Method(n.GetType(), "_Ready");
            if (ready != null)
            {
                ready.Invoke(n, null);
                RestoreLogger.Info($"[Revive] re-invoked _Ready on {label} ({n.GetType().Name})");
            }
        }
        catch (Exception ex) { RestoreLogger.Warn($"[Revive] _Ready re-invoke on {label} failed: {ex.GetType().Name}:{ex.Message}"); }
    }

    private static IEnumerable<Godot.Node> WalkNodeTree(Godot.Node root)
    {
        var stack = new Stack<Godot.Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            try
            {
                foreach (var c in n.GetChildren()) stack.Push(c);
            }
            catch { }
        }
    }

    /// <summary>
    /// Destroy any NCreature still pointing at this Creature. Zombies live on
    /// in two places after a death: (a) the active map returned by
    /// `room.GetCreatureNode(creature)` if `RemoveCreatureNode` hasn't run yet,
    /// (b) `_removingCreatureNodes` if it has. Both have a freed spine skeleton
    /// so neither is rebuildable — we have to free them and let `room.AddCreature`
    /// instantiate a fresh node from the monster's prefab.
    ///
    /// Uses `Free()` (immediate) instead of `QueueFree()` because the next call
    /// (`room.AddCreature`) runs in the same frame and we need the lookups to
    /// see no pre-existing node. Wrapped in try/catch — if Godot rejects Free
    /// (signals mid-emit), we fall back to detaching + QueueFree so at minimum
    /// the zombie won't be reused by `room.AddCreature`'s lookup.
    /// </summary>
    internal static void DestroyZombieNCreatures(
        MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom room,
        Creature creature,
        uint combatId)
    {
        var zombies = new List<MegaCrit.Sts2.Core.Nodes.Combat.NCreature>();

        try
        {
            if (room.GetCreatureNode(creature) is { } active)
                zombies.Add(active);
        }
        catch { }

        try
        {
            if (ReflectionCache.NcrRemovingNodesField?.GetValue(room)
                is System.Collections.IEnumerable removing)
            {
                foreach (var item in removing)
                {
                    if (item is not MegaCrit.Sts2.Core.Nodes.Combat.NCreature nc) continue;
                    try
                    {
                        if (ReflectionCache.NCreatureEntityProp?.GetValue(nc) is Creature ent
                            && ReferenceEquals(ent, creature)
                            && !zombies.Contains(nc))
                            zombies.Add(nc);
                    }
                    catch { }
                }
            }
        }
        catch { }

        // Also scan the EnemyContainer / AllyContainer scene-tree directly —
        // a zombie may have been detached from the active map and the removing
        // list but still in the scene tree (mid-QueueFree).
        try
        {
            foreach (var child in EnumerateAllNCreatureNodes(room))
            {
                if (zombies.Contains(child)) continue;
                if (ReflectionCache.NCreatureEntityProp?.GetValue(child) is Creature ent
                    && ReferenceEquals(ent, creature))
                    zombies.Add(child);
            }
        }
        catch { }

        var zombieIds = new List<ulong>();
        foreach (var z in zombies)
        {
            try { zombieIds.Add(z.GetInstanceId()); } catch { }
        }
        RestoreLogger.Info($"[Revive] id={combatId} found {zombies.Count} pre-existing NCreature reference(s) before re-add: ids=[{string.Join(",", zombieIds)}]");

        foreach (var z in zombies)
        {
            try { room.RemoveCreatureNode(z); } catch { }

            try
            {
                var parent = z.GetParent();
                parent?.RemoveChild(z);
            }
            catch { }

            // Also drop from `_removingCreatureNodes` so room.AddCreature
            // doesn't re-promote it.
            try
            {
                if (ReflectionCache.NcrRemovingNodesField?.GetValue(room)
                    is System.Collections.IList removingList)
                {
                    while (removingList.Contains(z)) removingList.Remove(z);
                }
            }
            catch { }

            try { z.Free(); }
            catch (Exception ex)
            {
                RestoreLogger.Warn($"[Revive] Free zombie failed ({ex.GetType().Name}); falling back to QueueFree");
                try { z.QueueFree(); } catch { }
            }
        }

        // Generic cache eviction — walk every field on NCombatRoom that holds
        // a Dictionary or List, and drop entries keyed/valued by the dead
        // creature or its zombie node. Even after Free()ing the zombie, the
        // cache may still hold the entry; AddCreature would then early-return
        // with no instantiation, leaving us with the freed reference and no
        // visible sprite.
        EvictCreatureFromAllRoomCaches(room, creature, zombies, combatId);
    }

    private static IEnumerable<MegaCrit.Sts2.Core.Nodes.Combat.NCreature> EnumerateAllNCreatureNodes(Godot.Node root)
    {
        var stack = new Stack<Godot.Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n is MegaCrit.Sts2.Core.Nodes.Combat.NCreature nc) yield return nc;
            try
            {
                foreach (var c in n.GetChildren()) stack.Push(c);
            }
            catch { }
        }
    }

    private static void EvictCreatureFromAllRoomCaches(
        MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom room,
        Creature creature,
        List<MegaCrit.Sts2.Core.Nodes.Combat.NCreature> zombies,
        uint combatId)
    {
        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic
                              | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        int evicted = 0;
        var zombieSet = new HashSet<object>(zombies);

        bool ShouldEvict(object? key, object? value)
        {
            if (key != null && (ReferenceEquals(key, creature) || zombieSet.Contains(key))) return true;
            if (value != null && (ReferenceEquals(value, creature) || zombieSet.Contains(value))) return true;
            return false;
        }

        try
        {
            // Walk the inheritance chain — NCombatRoom is a thin subclass and
            // its caches live in a base class (NRoom or similar). DeclaredOnly
            // on the leaf type misses them.
            for (var ct = room.GetType(); ct != null && ct != typeof(object); ct = ct.BaseType)
            {
                foreach (var f in ct.GetFields(F))
                {
                    object? raw;
                    try { raw = f.GetValue(room); } catch { continue; }
                    if (raw == null) continue;

                    string fid = $"{ct.Name}.{f.Name}";

                    // Dictionary<TKey, TValue>
                    if (raw is System.Collections.IDictionary dict)
                    {
                        var keysToRemove = new List<object>();
                        try
                        {
                            foreach (System.Collections.DictionaryEntry e in dict)
                                if (ShouldEvict(e.Key, e.Value)) keysToRemove.Add(e.Key);
                        }
                        catch { }
                        foreach (var k in keysToRemove)
                        {
                            try { dict.Remove(k); evicted++; } catch { }
                        }
                        if (keysToRemove.Count > 0)
                            RestoreLogger.Info($"[Revive] evicted {keysToRemove.Count} from {fid}");
                    }
                    // List / IList
                    else if (raw is System.Collections.IList list && raw is not Array)
                    {
                        int removed = 0;
                        try
                        {
                            for (int i = list.Count - 1; i >= 0; i--)
                            {
                                if (ShouldEvict(null, list[i]))
                                {
                                    try { list.RemoveAt(i); removed++; evicted++; } catch { }
                                }
                            }
                        }
                        catch { }
                        if (removed > 0)
                            RestoreLogger.Info($"[Revive] evicted {removed} from {fid}");
                    }
                    // Direct reference field (e.g. _lastSpawnedCreature)
                    else if (raw is MegaCrit.Sts2.Core.Nodes.Combat.NCreature directNc
                             && zombieSet.Contains(directNc))
                    {
                        try { f.SetValue(room, null); evicted++; RestoreLogger.Info($"[Revive] cleared {fid}"); }
                        catch { }
                    }
                    else if (raw is Creature directCreature && ReferenceEquals(directCreature, creature))
                    {
                        try { f.SetValue(room, null); evicted++; RestoreLogger.Info($"[Revive] cleared {fid}"); }
                        catch { }
                    }
                }
            }
        }
        catch (Exception ex) { RestoreLogger.Warn($"[Revive] cache eviction failed: {ex.Message}"); }

        RestoreLogger.Info($"[Revive] id={combatId} cache eviction total={evicted}");
    }

    /// <summary>
    /// Dump everything we can read off a freshly revived NCreature node — Body
    /// presence, scene-tree state, child node count, NCreatureVisuals presence,
    /// spine animation. The user reports "empty space" after kill+undo even
    /// though our log says `node=exists`; this lets us see WHY the node isn't
    /// rendering: missing Body, scale-0, off-screen, no spine skeleton, etc.
    /// </summary>
    private static void DumpReviveDiagnostics(uint combatId, MegaCrit.Sts2.Core.Nodes.Combat.NCreature node)
    {
        if (!RestoreLogger.EnableInfoLogging) return;
        try
        {
            string parent = "?";
            try { parent = node.GetParent()?.Name ?? "<orphan>"; } catch { }
            bool inTree = false;
            try { inTree = node.IsInsideTree(); } catch { }

            string bodyDiag = "no body";
            try
            {
                var body = node.Body;
                if (body != null)
                {
                    bodyDiag = $"body pos={body.Position} scale={body.Scale} mod={body.Modulate} vis={body.Visible} childCount={body.GetChildCount()}";
                }
            }
            catch (Exception ex) { bodyDiag = $"body err={ex.GetType().Name}:{ex.Message}"; }

            // Walk the subtree and tally type-name buckets so we can compare a
            // working creature vs. the broken revive shell.
            var typeCounts = new Dictionary<string, int>();
            object? visualsObj = null;
            try
            {
                var visualsType = ReflectionCache.NCreatureVisualsType;
                var stack = new Stack<Godot.Node>();
                stack.Push(node);
                while (stack.Count > 0)
                {
                    var n = stack.Pop();
                    var tn = n.GetType().Name;
                    typeCounts[tn] = typeCounts.TryGetValue(tn, out var v) ? v + 1 : 1;
                    if (visualsObj == null && visualsType != null && visualsType.IsInstanceOfType(n))
                        visualsObj = n;
                    foreach (var c in n.GetChildren()) stack.Push(c);
                }
            }
            catch (Exception ex) { RestoreLogger.Warn($"[Revive] tree walk err: {ex.Message}"); }

            // Probe MegaSprite (the actual spine renderer) — if its underlying
            // skeleton is null, the body is in-tree but has nothing to render
            // even with correct transforms. That's the smoking gun for
            // "completely empty" after revive.
            string spriteDiag = "<no visuals>";
            if (visualsObj != null)
            {
                try
                {
                    var spineBodyProp = HarmonyLib.AccessTools.Property(
                        ReflectionCache.NCreatureVisualsType!, "SpineBody");
                    var megaSprite = spineBodyProp?.GetValue(visualsObj);
                    if (megaSprite == null) spriteDiag = "spineBody=NULL";
                    else
                    {
                        var skel = ReflectionCache.MegaSpriteGetSkeletonMethod?.Invoke(megaSprite, null);
                        bool sprInTree = false;
                        try { if (megaSprite is Godot.Node mn) sprInTree = mn.IsInsideTree(); } catch { }
                        bool sprVis = false;
                        try { if (megaSprite is Godot.CanvasItem ci) sprVis = ci.Visible; } catch { }
                        spriteDiag = $"spineBody={megaSprite.GetType().Name} skeleton={(skel == null ? "NULL" : skel.GetType().Name)} inTree={sprInTree} vis={sprVis}";
                    }
                }
                catch (Exception ex) { spriteDiag = $"spineBody err={ex.GetType().Name}:{ex.Message}"; }
            }
            RestoreLogger.Info($"[Revive] diag id={combatId} {spriteDiag}");

            // Probe NCreatureVisuals.SpineAnimation directly — `spineNodes=0`
            // may have been a wrong heuristic if spine is property-attached
            // rather than a child node.
            string spineDiag = "<no visuals>";
            if (visualsObj != null)
            {
                try
                {
                    var spine = ReflectionCache.NCVSpineAnimationProp?.GetValue(visualsObj);
                    if (spine == null) spineDiag = "spine=NULL";
                    else
                    {
                        string track0 = "?";
                        try
                        {
                            var t = ReflectionCache.SpineGetCurrentTrackMethod?.Invoke(spine, new object[] { 0 });
                            var a = t == null ? null : ReflectionCache.TrackGetAnimationMethod?.Invoke(t, null);
                            var n = a == null ? null
                                : HarmonyLib.AccessTools.Method(a.GetType(), "GetName")?.Invoke(a, null) as string;
                            track0 = n ?? "<no track>";
                        }
                        catch { }
                        spineDiag = $"spine={spine.GetType().Name} track0='{track0}'";
                    }
                }
                catch (Exception ex) { spineDiag = $"spine err={ex.GetType().Name}:{ex.Message}"; }
            }

            // Truncated type-count summary — which types are present and how
            // many. A healthy creature has Mega* spine binding nodes; a broken
            // shell will be missing them.
            var topTypes = string.Join(",", typeCounts
                .OrderByDescending(kv => kv.Value)
                .Take(15)
                .Select(kv => $"{kv.Key}={kv.Value}"));

            RestoreLogger.Info($"[Revive] diag id={combatId} parent={parent} inTree={inTree} " +
                $"nodePos={node.GlobalPosition} nodeVis={node.Visible} mod={node.Modulate} " +
                $"descendants={typeCounts.Values.Sum()} | {bodyDiag}");
            RestoreLogger.Info($"[Revive] diag id={combatId} {spineDiag}");
            RestoreLogger.Info($"[Revive] diag id={combatId} types: {topTypes}");

            // Visuals parent check — ensures reparent fix worked. Healthy
            // baseline expects Visuals.parent == NCreature.
            try
            {
                var vis = node.Visuals as Godot.Node;
                if (vis != null)
                {
                    var visParent = vis.GetParent();
                    bool underNCreature = ReferenceEquals(visParent, node);
                    string parentDesc = "<null>";
                    if (visParent != null)
                    {
                        try { parentDesc = $"{visParent.GetType().Name}:'{visParent.Name}'"; }
                        catch { parentDesc = visParent.GetType().Name; }
                    }
                    RestoreLogger.Info($"[Revive] diag id={combatId} Visuals.parent={parentDesc} underNCreature={underNCreature}");
                }
            }
            catch (Exception ex) { RestoreLogger.Warn($"[Revive] diag visuals-parent: {ex.Message}"); }

            // Field-level dump — narrow down which member ref on NCreature /
            // NCreatureVisuals is null on the revived shell (vs healthy
            // baseline). Type-bucket diff said descendants=86 vs 95; the
            // missing 9 children should appear as null fields here.
            DumpInstanceFields(node, $"[FieldDiff] id={combatId} NCreature");
            if (visualsObj != null)
                DumpInstanceFields(visualsObj, $"[FieldDiff] id={combatId} NCreatureVisuals");

            // Death-anim survivor probe: instance fields show Tween-typed
            // member refs but DON'T show SceneTree-level tweens (created via
            // node.CreateTween() and tracked by Godot's tween manager rather
            // than stored as a field). Plus any Timer child node that might
            // be queued to fire QueueFree at death-anim end.
            DumpActiveTweensAndTimers(node, combatId);
        }
        catch (Exception ex) { RestoreLogger.Warn($"[Revive] diag failed: {ex.Message}"); }
    }

    /// <summary>
    /// Dump SceneTree-level tweens bound to anything in the revived NCreature
    /// subtree, plus every Timer child node with its time-left / autostart /
    /// one-shot state. After in-place revive, the most likely culprits for
    /// "death animation completes anyway and fades creature back to invisible":
    ///   - a fade Tween bound to NCreatureVisuals/Body that animates Modulate
    ///   - a Timer that fires QueueFree on timeout
    /// Both leave the field dump clean (they're sibling/child nodes, not
    /// stored as fields), so we need this targeted enumeration to find them.
    /// </summary>
    private static void DumpActiveTweensAndTimers(Godot.Node root, uint combatId)
    {
        if (!RestoreLogger.EnableInfoLogging) return;
        try
        {
            // Step 1: collect every Node ref under the revived creature so we
            // can match Tween.BoundNode against this set.
            var subtreeIds = new HashSet<ulong>();
            int subtreeCount = 0;
            foreach (var n in WalkNodeTree(root))
            {
                try { subtreeIds.Add(n.GetInstanceId()); subtreeCount++; } catch { }
            }

            // Step 2: enumerate Timer children directly. Timer is a Godot.Node
            // so it shows up in the walk; we just filter and pull state.
            int timerCount = 0;
            foreach (var n in WalkNodeTree(root))
            {
                if (n is not Godot.Timer timer) continue;
                timerCount++;
                string nm = "?";
                try { nm = timer.Name.ToString(); } catch { }
                bool stopped = true;
                double timeLeft = -1, waitTime = -1;
                bool autostart = false, oneShot = false;
                try { stopped = timer.IsStopped(); } catch { }
                try { timeLeft = timer.TimeLeft; } catch { }
                try { waitTime = timer.WaitTime; } catch { }
                try { autostart = timer.Autostart; } catch { }
                try { oneShot = timer.OneShot; } catch { }
                RestoreLogger.Info($"[DeathProbe] id={combatId} Timer name='{nm}' stopped={stopped} " +
                    $"timeLeft={timeLeft:F3}/{waitTime:F3} autostart={autostart} oneShot={oneShot}");
            }
            if (timerCount == 0)
                RestoreLogger.Info($"[DeathProbe] id={combatId} no Timer nodes in subtree (count={subtreeCount})");

            // Step 3: SceneTree active tween enumeration. Godot 4 exposes
            // `SceneTree.GetProcessedTweens()` — but the binding name and
            // existence vary across versions. We probe via reflection so a
            // missing method just logs once instead of throwing.
            try
            {
                var tree = root.GetTree();
                if (tree == null) { RestoreLogger.Info($"[DeathProbe] id={combatId} GetTree=null"); return; }

                var treeType = tree.GetType();
                MethodInfo? getTweensMethod = null;
                foreach (var name in new[] { "GetProcessedTweens", "get_processed_tweens" })
                {
                    var m = treeType.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic
                        | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                    if (m != null) { getTweensMethod = m; break; }
                }
                if (getTweensMethod == null)
                {
                    RestoreLogger.Info($"[DeathProbe] id={combatId} SceneTree.GetProcessedTweens not found — " +
                        $"available methods (Tween-related): " +
                        string.Join(",", treeType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                            | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
                            .Where(mi => mi.Name.IndexOf("Tween", StringComparison.OrdinalIgnoreCase) >= 0)
                            .Select(mi => mi.Name).Take(15)));
                    return;
                }

                var tweensObj = getTweensMethod.Invoke(tree, null);
                int total = 0, bound = 0;
                if (tweensObj is System.Collections.IEnumerable enumerable)
                {
                    foreach (var tw in enumerable)
                    {
                        total++;
                        if (tw == null) continue;
                        // Tween.GetBoundNode() / IsRunning() / IsValid() — try
                        // all variants since the C# binding API name shifts.
                        var twType = tw.GetType();
                        Godot.Node? boundNode = null;
                        bool isRunning = false, isValid = false;
                        foreach (var n in new[] { "GetBoundNode", "get_bound_node" })
                        {
                            var m = twType.GetMethod(n, BindingFlags.Public | BindingFlags.NonPublic
                                | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                            if (m != null) { boundNode = m.Invoke(tw, null) as Godot.Node; break; }
                        }
                        try
                        {
                            var rm = twType.GetMethod("IsRunning", BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                            if (rm != null) isRunning = (bool)(rm.Invoke(tw, null) ?? false);
                            var vm = twType.GetMethod("IsValid", BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                            if (vm != null) isValid = (bool)(vm.Invoke(tw, null) ?? false);
                        }
                        catch { }

                        ulong boundId = 0;
                        try { if (boundNode != null) boundId = boundNode.GetInstanceId(); } catch { }
                        bool inSubtree = boundId != 0 && subtreeIds.Contains(boundId);
                        if (inSubtree) bound++;

                        if (inSubtree || (boundNode == null && isRunning))
                        {
                            string boundName = "<no bound>";
                            if (boundNode != null)
                            {
                                try { boundName = $"{boundNode.GetType().Name}:'{boundNode.Name}'"; }
                                catch { boundName = boundNode.GetType().Name; }
                            }
                            RestoreLogger.Info($"[DeathProbe] id={combatId} Tween bound={boundName} " +
                                $"running={isRunning} valid={isValid} inSubtree={inSubtree}");
                        }
                    }
                }
                RestoreLogger.Info($"[DeathProbe] id={combatId} active tweens total={total} subtreeBound={bound}");
            }
            catch (Exception ex)
            {
                RestoreLogger.Warn($"[DeathProbe] id={combatId} tween enum failed: {ex.GetType().Name}:{ex.Message}");
            }
        }
        catch (Exception ex)
        {
            RestoreLogger.Warn($"[DeathProbe] id={combatId} failed: {ex.GetType().Name}:{ex.Message}");
        }
    }

    /// <summary>
    /// Dump every instance field declared on the given object's type chain,
    /// stopping at the Godot/System boundary. For each field, log:
    ///   - null status
    ///   - if Godot.Node: type name + node name + IsInsideTree
    ///   - if collection: type + Count
    ///   - else: type + truncated ToString()
    /// Output is batched in chunks so log lines don't blow past the line limit.
    /// </summary>
    private static void DumpInstanceFields(object obj, string prefix)
    {
        if (!RestoreLogger.EnableInfoLogging) return;
        try
        {
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic
                                  | BindingFlags.Instance | BindingFlags.DeclaredOnly;

            var lines = new List<string>();
            for (var t = obj.GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                // Stop at the engine boundary — Godot.Node has hundreds of
                // internal fields (caches, signal lists, viewports) that don't
                // help us diagnose missing creature visuals.
                var ns = t.Namespace ?? "";
                if (ns.StartsWith("Godot", StringComparison.Ordinal)
                    || ns.StartsWith("System", StringComparison.Ordinal))
                    break;

                foreach (var f in t.GetFields(F))
                {
                    object? val;
                    try { val = f.GetValue(obj); }
                    catch (Exception ex) { lines.Add($"[{t.Name}] {f.Name}=<read-err:{ex.GetType().Name}>"); continue; }

                    string desc;
                    if (val == null)
                    {
                        desc = $"NULL:{f.FieldType.Name}";
                    }
                    else if (val is Godot.Node n)
                    {
                        bool inTree = false;
                        try { inTree = n.IsInsideTree(); } catch { }
                        string nm = "?";
                        try { nm = n.Name.ToString(); } catch { }
                        desc = $"{n.GetType().Name}(name='{nm}',inTree={inTree})";
                    }
                    else if (val is System.Collections.ICollection col)
                    {
                        desc = $"{val.GetType().Name}(count={col.Count})";
                    }
                    else
                    {
                        string s;
                        try { s = val.ToString() ?? ""; } catch { s = "<tostr-err>"; }
                        if (s.Length > 40) s = s.Substring(0, 40) + "…";
                        desc = $"{val.GetType().Name}({s})";
                    }
                    lines.Add($"[{t.Name}] {f.Name}={desc}");
                }
            }

            RestoreLogger.Info($"{prefix} fields ({lines.Count}):");
            const int chunk = 4;
            for (int i = 0; i < lines.Count; i += chunk)
                RestoreLogger.Info($"  {string.Join(" | ", lines.Skip(i).Take(chunk))}");
        }
        catch (Exception ex) { RestoreLogger.Warn($"{prefix} field dump failed: {ex.Message}"); }
    }

    /// <summary>
    /// Same dump shape as the revive diag, but for a creature that's currently
    /// alive — gives us a reference baseline to compare against. Called once
    /// per combat from the first capture so we know what a healthy creature
    /// tree looks like before any kill happens.
    /// </summary>
    public static void DumpHealthyCreatureBaseline(MegaCrit.Sts2.Core.Nodes.Combat.NCreature node, uint combatId)
    {
        DumpReviveDiagnostics(combatId, node);
    }

    private static void SetCombatStateOnCreature(Creature creature, CombatState cs)
    {
        var prop = AccessTools.Property(typeof(Creature), "CombatState");
        if (prop?.CanWrite == true)
        {
            try { prop.SetValue(creature, cs); return; } catch { }
        }
        var f = AccessTools.Field(typeof(Creature), "<CombatState>k__BackingField")
            ?? AccessTools.Field(typeof(Creature), "_combatState");
        f?.SetValue(creature, cs);
    }

    private static int RemoveCreaturesNotIn(HashSet<uint> snapIds, System.Collections.IList list)
    {
        int removed = 0;
        var room = MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom.Instance;

        // Walk backwards so we can remove in place.
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (list[i] is not Creature c) continue;
            if (!c.CombatId.HasValue) continue;
            if (snapIds.Contains(c.CombatId.Value)) continue;

            RestoreLogger.Info($"[Roster] remove summoned id={c.CombatId.Value}");

            // Free its visual node first.
            if (room != null)
            {
                var node = room.GetCreatureNode(c);
                if (node != null)
                {
                    try
                    {
                        node.Visible = false;
                        room.RemoveCreatureNode(node);
                        node.QueueFree();
                    }
                    catch (Exception ex) { RestoreLogger.Warn($"[Roster] visual removal failed: {ex.Message}"); }
                }
            }

            list.RemoveAt(i);
            removed++;
        }
        return removed;
    }

    private static bool IsPet(Creature c, CombatSnapshot snap)
        => c.CombatId.HasValue && snap.PetCombatIds.Contains(c.CombatId.Value);

    private static void RestoreCreatures(CombatSnapshot snap, CombatState cs)
    {
        foreach (var saved in snap.Creatures)
        {
            // Use saved.Ref directly — looking up via cs.Creatures fails if the
            // creature was removed from the rosters on death. RestoreCreatureRoster
            // re-added it; even so, the saved.Ref is the canonical pointer.
            var live = saved.Ref;
            if (live == null) continue;

            int oldHp = (int)(ReflectionCache.CreatureHpField.GetValue(live) ?? 0);
            int oldMaxHp = (int)(ReflectionCache.CreatureMaxHpField.GetValue(live) ?? 0);
            int oldBlock = (int)(ReflectionCache.CreatureBlockField.GetValue(live) ?? 0);

            ReflectionCache.CreatureHpField.SetValue(live, saved.CurrentHp);
            ReflectionCache.CreatureMaxHpField.SetValue(live, saved.MaxHp);
            ReflectionCache.CreatureBlockField.SetValue(live, saved.Block);

            // If the game uses an explicit IsDead/_isDead flag separate from HP,
            // reset it. The property has no setter; backing field name varies, so
            // we try common names.
            ResetIsDeadIfPresent(live, saved.IsDead);

            // Fire the *Changed events so visual subscribers refresh to the
            // restored values (HP bars, block icons, etc.).
            FireDelegateField(live, ReflectionCache.CreatureCurrentHpChangedField, oldHp, saved.CurrentHp);
            FireDelegateField(live, ReflectionCache.CreatureMaxHpChangedField, oldMaxHp, saved.MaxHp);
            FireDelegateField(live, ReflectionCache.CreatureBlockChangedField, oldBlock, saved.Block);

            RestoreCreaturePowers(live, saved);

            if (live.Monster != null && saved.MonsterRng is { } ser2)
            {
                ReflectionCache.MonsterRngField?.SetValue(live.Monster, new Rng(ser2));
            }

            if (live.Monster != null && saved.MonsterMove.HasValue)
                RestoreMonsterMove(live.Monster, saved.MonsterMove.Value);
            if (live.Monster != null && saved.MonsterFields != null)
                RestoreMonsterFields(live.Monster, saved.MonsterFields);
        }
    }

    /// <summary>
    /// Apply the captured per-subtype primitive field values back onto the
    /// live monster. Skips any field whose name doesn't exist on the live
    /// type (defensive — class definitions match across runs but be safe).
    /// </summary>
    private static void RestoreMonsterFields(
        MegaCrit.Sts2.Core.Models.MonsterModel monster,
        Dictionary<string, object?> snap)
    {
        int set = 0;
        try
        {
            for (var t = monster.GetType(); t != null && t != typeof(object) && t != typeof(MegaCrit.Sts2.Core.Models.MonsterModel); t = t.BaseType)
            {
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic
                                              | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (f.IsLiteral || f.IsInitOnly) continue;
                    var ft = f.FieldType;
                    if (!(ft.IsPrimitive || ft.IsEnum)) continue;
                    var key = (t.FullName ?? t.Name) + "::" + f.Name;
                    if (!snap.TryGetValue(key, out var val)) continue;
                    try { f.SetValue(monster, val); set++; }
                    catch { }
                }
            }
        }
        catch (Exception ex) { RestoreLogger.Warn($"[Monster] field restore: {ex.Message}"); }
        if (set > 0)
        {
            // Verbose dump of restored values so we can pinpoint regressions
            // like "Slug ravenous trigger anim skipped because _isRavenous
            // restored as true". Logs the value of every primitive/enum field
            // we touched.
            var values = new List<string>();
            foreach (var kv in snap)
            {
                values.Add($"{kv.Key.Substring(kv.Key.LastIndexOf("::", StringComparison.Ordinal) + 2)}={kv.Value}");
            }
            RestoreLogger.Info($"[Monster] restored {set} subtype field(s) on {monster.GetType().Name}: {string.Join(", ", values)}");
        }
    }

    /// <summary>
    /// Restore monster move-state-machine fully: PerformedFirstMove + SpawnedThisTurn
    /// + CurrentState (via ForceCurrentState) + StateLog + per-state PerformedAtLeastOnce
    /// + NextMove (via property setter, NOT SetMoveImmediate which has a transition gate
    /// that blocks restoration of REATTACH/RESPAWN-style moves).
    /// </summary>
    private static void RestoreMonsterMove(MonsterModel monster, MonsterMoveSnapshot saved)
    {
        var sm = monster.MoveStateMachine;
        if (sm == null) return;

        ReflectionCache.SmPerformedFirstMoveField?.SetValue(sm, saved.PerformedFirstMove);
        ReflectionCache.MonsterSpawnedField?.SetValue(monster, saved.SpawnedThisTurn);

        if (ReflectionCache.SmStatesProp?.GetValue(sm) is not System.Collections.IDictionary states)
            return;

        // Current state. Prefer dict lookup (named built-in states), fall back
        // to the strong ref captured at snapshot time. Strong ref handles
        // dynamically-created states (e.g. Stun's transient "STUNNED" state)
        // that are NEVER in States dict — without this, post-turn-cross undo
        // loses stun and the monster reverts to its original move pattern.
        object? current = null;
        if (saved.CurrentStateId != null && states.Contains(saved.CurrentStateId))
            current = states[saved.CurrentStateId];
        else if (saved.CurrentStateRef != null
                 && ReflectionCache.MonsterStateType?.IsInstanceOfType(saved.CurrentStateRef) == true)
            current = saved.CurrentStateRef;
        if (current != null)
        {
            try { ReflectionCache.SmForceCurrentStateMethod?.Invoke(sm, new[] { current }); }
            catch (Exception ex) { RestoreLogger.Warn($"[Monster] ForceCurrentState failed: {ex.Message}"); }
        }

        // StateLog.
        if (ReflectionCache.SmStateLogProp?.GetValue(sm) is System.Collections.IList stateLog
            && saved.StateLogIds != null)
        {
            stateLog.Clear();
            foreach (var id in saved.StateLogIds)
                if (states.Contains(id))
                    stateLog.Add(states[id]!);
        }

        // Per-state PerformedAtLeastOnce.
        if (ReflectionCache.MoveStatePerformedField != null
            && saved.MovePerformedAtLeastOnce != null)
        {
            foreach (System.Collections.DictionaryEntry e in states)
            {
                if (e.Key is string key
                    && saved.MovePerformedAtLeastOnce.TryGetValue(key, out var p)
                    && e.Value != null)
                {
                    try { ReflectionCache.MoveStatePerformedField.SetValue(e.Value, p); }
                    catch { }
                }
            }
        }

        // NextMove — direct property set, bypasses CanTransitionAway gating.
        // Same dual-strategy as CurrentState: dict-by-id first, strong ref
        // fallback. Critical for stun: the STUNNED MoveState is held only
        // via the NextMove property, never the States dict.
        object? nextState = null;
        if (saved.NextMoveStateId != null && states.Contains(saved.NextMoveStateId))
            nextState = states[saved.NextMoveStateId];
        else if (saved.NextMoveRef != null
                 && ReflectionCache.MonsterStateType?.IsInstanceOfType(saved.NextMoveRef) == true)
            nextState = saved.NextMoveRef;
        if (nextState != null
            && ReflectionCache.MonsterStateType?.IsInstanceOfType(nextState) == true)
        {
            try { ReflectionCache.NextMoveProp?.SetValue(monster, nextState); }
            catch (Exception ex) { RestoreLogger.Warn($"[Monster] NextMove set failed: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Invoke an auto-event's backing delegate via reflection so subscribed
    /// listeners (visuals) receive the synthetic event. Used after reflection-
    /// setting model fields, where the normal property-setter path that fires
    /// the event was bypassed.
    /// </summary>
    private static void FireDelegateField(object target, System.Reflection.FieldInfo? field, params object?[] args)
    {
        if (field == null) return;
        try
        {
            if (field.GetValue(target) is Delegate d) d.DynamicInvoke(args);
        }
        catch (Exception ex) { RestoreLogger.Warn($"[Restore] event fire ({field.Name}) failed: {ex.Message}"); }
    }

    private static void ResetIsDeadIfPresent(Creature live, bool wasDeadInSnapshot)
    {
        // Try the common backing-field names. We always force live to match the
        // captured IsDead value (usually false — i.e. revive).
        foreach (var name in new[] { "<IsDead>k__BackingField", "_isDead", "_dead", "_isDying" })
        {
            var f = AccessTools.Field(typeof(Creature), name);
            if (f != null) { try { f.SetValue(live, wasDeadInSnapshot); } catch { } }
        }
    }

    private static void RestoreSyncState(CombatSnapshot snap)
    {
        var rm = RunManager.Instance;
        if (rm == null) return;

        // ActionQueueSynchronizer.CombatState — force PlayPhase always (we're
        // restoring into a player-turn moment by guard contract).
        var syncr = rm.ActionQueueSynchronizer;
        if (syncr != null)
        {
            var prop = AccessTools.Property(syncr.GetType(), "CombatState");
            var value = MegaCrit.Sts2.Core.Entities.Multiplayer.ActionSynchronizerCombatState.PlayPhase;
            if (prop?.CanWrite == true)
                try { prop.SetValue(syncr, value); } catch { }
            else
            {
                var f = AccessTools.Field(syncr.GetType(), "<CombatState>k__BackingField")
                    ?? AccessTools.Field(syncr.GetType(), "_combatState");
                f?.SetValue(syncr, value);
            }
        }

        // CombatManager.IsPaused → false (resume any paused execution).
        var cm = CombatManager.Instance;
        if (cm != null)
        {
            var pausedProp = AccessTools.Property(typeof(CombatManager), "IsPaused");
            if (pausedProp?.CanWrite == true)
                try { pausedProp.SetValue(cm, false); } catch { }
        }

        // Reset per-queue isPaused = false. We DELIBERATELY don't drain the
        // _actions lists — clearing actions mid-execution makes the executor
        // wait forever for a destroyed action, freezing the game on the
        // card-being-used state. Pause resets are safe; queue contents are not.
        var aqSet = rm.ActionQueueSet;
        if (aqSet != null)
        {
            var queuesField = AccessTools.Field(aqSet.GetType(), "_actionQueues");
            if (queuesField?.GetValue(aqSet) is System.Collections.IEnumerable queues)
            {
                foreach (var q in queues)
                {
                    if (q == null) continue;
                    var pausedField = AccessTools.Field(q.GetType(), "isPaused");
                    try { pausedField?.SetValue(q, false); } catch { }
                }
            }
        }

        // ActionExecutor.IsPaused = false — without this, the executor stays
        // paused after enemy-turn flow and won't pick up new player actions.
        var executor = rm.ActionExecutor;
        if (executor != null)
        {
            var prop = AccessTools.Property(executor.GetType(), "IsPaused");
            if (prop?.CanWrite == true)
                try { prop.SetValue(executor, false); } catch { }
            else
            {
                var f = AccessTools.Field(executor.GetType(), "<IsPaused>k__BackingField")
                    ?? AccessTools.Field(executor.GetType(), "_isPaused");
                try { f?.SetValue(executor, false); } catch { }
            }
        }
    }

    private static void RestoreCreaturePowers(Creature creature, CreatureSnapshot saved)
    {
        var liveList = ReflectionCache.CreaturePowersField.GetValue(creature)
            as System.Collections.IList;
        if (liveList == null) return;

        // Strategy: full Hook-lifecycle resync. v0.0.4-attempt only Activated
        // hooks for powers that were stripped from the live list, but the
        // user-reported Unrelenting bug (다음 공격 0코스트 효과가 undo 후
        // 살지 않음) revealed a third case: the game can keep the power in
        // the `_powers` list at amount=0 while still calling DeactivateHooks
        // — so by-Id lookup finds the power, our amount restore writes 1 back,
        // but the underlying Hook subscription stays cold. Result: the cost
        // modifier never fires on the next play.
        //
        // Resolution: always Deactivate every existing live power before
        // touching state, restore state from snapshot, then Activate every
        // power that ends up in the rebuilt list. Hook subscribers in
        // STS2 use HookSubscribers collections that no-op on duplicate
        // add and unhook-not-hooked, so the unconditional cycle is safe.
        //
        // Match snap → live by Ref equality, NOT by Id. Powers with
        // `IsInstanced = true` (Regent's OrbitPower, etc.) keep multiple
        // distinct instances on the same creature that all share an Id;
        // an Id-keyed dictionary collapses them to the last one inserted,
        // and every snap entry's Amount / _internalData then overwrites
        // the same live instance, "unifying" all N stacks to a single
        // recorded count after an undo. Ref-keyed lookup hits each
        // instance exactly once.
        var liveSet = new HashSet<PowerModel>();
        foreach (var item in liveList)
            if (item is PowerModel pm) liveSet.Add(pm);

        // Phase 1: Deactivate hooks on EVERY live power (whether it's going to
        // be retained, dropped, or replaced). Iterate liveList directly — the
        // prior by-Id loop missed all but one instance per Id for instanced
        // powers, leaving stale hook subscriptions on the rest.
        int deactivated = 0;
        foreach (var livePower in liveSet)
            if (TryDeactivatePowerHooks(livePower)) deactivated++;
        // Also deactivate any snapshot Refs whose live instance ISN'T in the
        // current list — those refs were stripped from the creature but may
        // still hold residual subscriptions (game's StripPowers may not have
        // deactivated all hook bindings consistently across power types).
        foreach (var s in saved.Powers)
        {
            if (s.Ref == null) continue;
            if (liveSet.Contains(s.Ref)) continue;  // already handled above
            if (TryDeactivatePowerHooks(s.Ref)) deactivated++;
        }

        liveList.Clear();
        var rebuilt = new List<PowerModel>();
        int reattached = 0;
        // Track which live instances have already been claimed by a snap
        // entry so a second snap entry with the same Id can't double-bind
        // to the same live instance (only matters when Ref matching falls
        // through to the reattach path, but cheap insurance regardless).
        var consumed = new HashSet<PowerModel>();
        foreach (var snapPower in saved.Powers)
        {
            PowerModel? live = null;
            // Primary lookup: snapshot Ref still alive AND in the current
            // live list AND not yet consumed by an earlier snap entry.
            if (snapPower.Ref != null && liveSet.Contains(snapPower.Ref)
                && !consumed.Contains(snapPower.Ref))
            {
                live = snapPower.Ref;
            }
            if (live == null)
            {
                // Ref isn't in the live list — most commonly the creature
                // died and the game stripped its `_powers` list, OR the
                // power's amount went to 0 and was removed. We held a
                // strong ref at snapshot time; reattach the same instance
                // by reflecting `_owner` (the public setter throws once
                // owner != null and value differs — and the game cleared
                // owner=null when stripping).
                if (snapPower.Ref != null && !consumed.Contains(snapPower.Ref))
                {
                    try
                    {
                        ReflectionCache.PowerOwnerField?.SetValue(snapPower.Ref, creature);
                        live = snapPower.Ref;
                        reattached++;
                    }
                    catch (Exception ex)
                    { RestoreLogger.Warn($"[Powers] reattach owner failed for {snapPower.Id.Entry}: {ex.Message}"); }
                }
                if (live == null) continue;
            }
            consumed.Add(live);
            ReflectionCache.PowerAmountField.SetValue(live, snapPower.Amount);
            ReflectionCache.PowerAmountOnTurnStartField.SetValue(live, snapPower.AmountOnTurnStart);
            ReflectionCache.PowerSkipField.SetValue(live, snapPower.SkipNextDurationTick);
            // Re-clone _internalData on restore so the snapshot keeps a private copy
            // (game mutates the live object after restore — without re-cloning, our
            // snapshot would be aliased to the live mutation).
            if (ReflectionCache.PowerInternalDataField != null)
                ReflectionCache.PowerInternalDataField.SetValue(
                    live, DeepCloner.CloneObject(snapPower.InternalDataClone));

            // Copy MutableClone() result fields back onto the live instance —
            // catches subtype private fields the explicit setters above don't
            // cover. Concrete trigger: SurroundedPower._facing (Kaiser Crab Boss
            // mechanic) — body.Scale rolls back via VisualBodyScale but _facing
            // would otherwise stay desynced from the visual, causing subsequent
            // attacks to skip the flip and the player to look "stuck" facing the
            // wrong way. Skips _owner/_canonicalInstance/Id (live identity must
            // not change) and _internalData (handled above; clone's value is
            // re-inited via DeepCloneFields and would lose state).
            if (snapPower.Clone != null && live.GetType() == snapPower.Clone.GetType())
            {
                foreach (var f in GetPowerCopyFields(live.GetType()))
                {
                    try { f.SetValue(live, f.GetValue(snapPower.Clone)); }
                    catch { }
                }
            }
            liveList.Add(live);
            rebuilt.Add(live);
        }

        // Phase 2: Activate hooks on every power now in the rebuilt list. Done
        // after the list is fully repopulated so any internal "is in owner._powers"
        // guard inside ActivateHooks sees the power as a current member.
        int activated = 0;
        foreach (var pm in rebuilt)
            if (TryActivatePowerHooks(pm)) activated++;

        if (deactivated > 0 || reattached > 0 || activated > 0)
            RestoreLogger.Info(
                $"[Powers] hook lifecycle: deactivated={deactivated} reattached={reattached} activated={activated}");

        // Player-creature only: emit a one-line summary of the resulting
        // Powers list so log shows FreeSkillPower / FreeAttackPower / etc.
        // state across snapshot/restore. Helps diagnose Pounce-style cost
        // modifier bugs without parsing the full per-power dump.
        if (creature.Side == CombatSide.Player)
        {
            var summary = new List<string>();
            foreach (var item in liveList)
                if (item is PowerModel pm)
                {
                    var amt = ReflectionCache.PowerAmountField.GetValue(pm);
                    summary.Add($"{pm.Id.Entry}={amt}");
                }
            RestoreLogger.Info($"[Powers] player after restore: [{string.Join(", ", summary)}]");
        }

        // Notify amount-changed hooks so display refreshes.
        foreach (var item in liveList)
            if (item is PowerModel pm)
                ReflectionCache.PowerInvokeAmountChangedMethod?.Invoke(pm, null);
    }

    private static bool TryActivatePowerHooks(PowerModel pm)
    {
        if (ReflectionCache.PowerActivateHooksMethod == null) return false;
        try
        {
            ReflectionCache.PowerActivateHooksMethod.Invoke(pm, null);
            return true;
        }
        catch (Exception ex)
        {
            RestoreLogger.Warn($"[Powers] ActivateHooks({pm.Id.Entry}) failed: {ex.Message}");
            return false;
        }
    }

    private static bool TryDeactivatePowerHooks(PowerModel pm)
    {
        if (ReflectionCache.PowerDeactivateHooksMethod == null) return false;
        try
        {
            ReflectionCache.PowerDeactivateHooksMethod.Invoke(pm, null);
            return true;
        }
        catch (Exception ex)
        {
            RestoreLogger.Warn($"[Powers] DeactivateHooks({pm.Id.Entry}) failed: {ex.Message}");
            return false;
        }
    }

    private static void RestoreRelics(CombatSnapshot snap, CombatState cs)
    {
        foreach (var rs in snap.Relics)
        {
            var live = rs.Ref;
            if (live == null) continue;

            ReflectionCache.RelicStackCountField?.SetValue(live, rs.StackCount);
            if (ReflectionCache.RelicStatusProperty?.CanWrite == true && rs.Status != null)
                ReflectionCache.RelicStatusProperty.SetValue(live, rs.Status);

            // Copy MutableClone() result fields back onto the live instance.
            // The clone was produced by the game's own RelicModel.MutableClone
            // at capture time, which chained DeepCloneFields overrides for the
            // subclass — so its mutable state (Stone Sword internals, Letter
            // Opener counters, etc.) is correctly isolated from subsequent
            // live mutations. Walk every instance field and write clone→live,
            // skipping identity-bearing/event/static fields that must stay
            // pinned to the live instance. This field-copy also restores
            // _dynamicVars (deep-cloned inside MutableClone), so no separate
            // DynamicVars restore is needed on this path.
            if (rs.Clone != null && live.GetType() == rs.Clone.GetType())
            {
                foreach (var f in GetRelicCopyFields(live.GetType()))
                {
                    try { f.SetValue(live, f.GetValue(rs.Clone)); }
                    catch { }
                }
            }
            else if (rs.DynamicVarsClone != null && ReflectionCache.RelicDynamicVarsField != null)
            {
                // Fallback only: MutableClone failed at capture, so there is no
                // clone to field-copy _dynamicVars from. Restore the reflection
                // clone captured for exactly this case (see CombatSnapshot.CaptureRelic).
                ReflectionCache.RelicDynamicVarsField.SetValue(
                    live, DeepCloner.CloneObject(rs.DynamicVarsClone));
            }
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, FieldInfo[]> _relicCopyFieldCache = new();

    private static FieldInfo[] GetRelicCopyFields(Type type)
        => _relicCopyFieldCache.GetOrAdd(type, BuildRelicCopyFields);

    private static FieldInfo[] BuildRelicCopyFields(Type type)
    {
        var list = new List<FieldInfo>();
        for (var t = type; t != null && t != typeof(object); t = t.BaseType)
        {
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic
                                          | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (f.IsLiteral || f.IsInitOnly) continue;
                // Identity / immutable-by-design fields the live must keep:
                if (f.Name is "_canonicalInstance" or "_owner") continue;
                if (f.Name is "<Id>k__BackingField" or "<IsMutable>k__BackingField"
                    or "<Category>k__BackingField" or "<Entry>k__BackingField") continue;
                // Skip event delegates — copying them would re-target subscribers
                // away from the live instance to the clone (which gets discarded).
                if (typeof(Delegate).IsAssignableFrom(f.FieldType)) continue;
                list.Add(f);
            }
        }
        return list.ToArray();
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, FieldInfo[]> _powerCopyFieldCache = new();

    private static FieldInfo[] GetPowerCopyFields(Type type)
        => _powerCopyFieldCache.GetOrAdd(type, BuildPowerCopyFields);

    private static FieldInfo[] BuildPowerCopyFields(Type type)
    {
        var list = new List<FieldInfo>();
        for (var t = type; t != null && t != typeof(object); t = t.BaseType)
        {
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic
                                          | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (f.IsLiteral || f.IsInitOnly) continue;
                // _internalData has its own DeepCloner round-trip path above; the
                // clone's value was re-initialized by PowerModel.DeepCloneFields
                // and would CLOBBER the captured state if copied here.
                if (f.Name is "_canonicalInstance" or "_owner" or "_internalData") continue;
                if (f.Name is "<Id>k__BackingField" or "<IsMutable>k__BackingField"
                    or "<Category>k__BackingField" or "<Entry>k__BackingField") continue;
                if (typeof(Delegate).IsAssignableFrom(f.FieldType)) continue;
                list.Add(f);
            }
        }
        return list.ToArray();
    }

    /// <summary>
    /// Restore the OrbQueue: replace `_orbs` list contents with the saved refs (so
    /// NOrb visual identity stays valid where possible) and copy mutable fields
    /// from each clone back onto the live orb. Also restores `Capacity` — without
    /// it, evolving relics that grew the slot mid-combat would lose the extra
    /// slot on undo. The visual rebuild (NOrb child nodes) lives in OrbRefresher;
    /// this function only touches model state.
    /// </summary>
    private static void RestoreOrbs(CombatSnapshot snap, CombatState cs)
    {
        if (!snap.HasOrbData) return;

        foreach (var ally in cs.Allies)
        {
            var player = ally.Player;
            if (player == null) continue;
            var pcs = player.PlayerCombatState;
            if (pcs == null) continue;

            var orbQueue = pcs.OrbQueue;
            if (orbQueue == null) return;

            if (ReflectionCache.OrbQueueOrbsField?.GetValue(orbQueue)
                is System.Collections.IList orbsList)
            {
                int oldCount = orbsList.Count;
                orbsList.Clear();
                foreach (var orb in snap.OrbRefs)
                {
                    if (snap.OrbClones.TryGetValue(orb, out var clone))
                        CopyOrbMutableFields(clone, orb);
                    orbsList.Add(orb);
                }
                RestoreLogger.Info($"[Orbs] {oldCount}→{snap.OrbRefs.Count} orbs (cap={snap.OrbCapacity})");
            }

            ReflectionCache.OrbQueueCapacityField?.SetValue(orbQueue, snap.OrbCapacity);
            return;
        }
    }

    /// <summary>
    /// Copy every mutable instance field from `from` to `to`, walking up the
    /// inheritance chain so subclass-only fields (DarkOrb._evokeVal,
    /// GlassOrb._passiveVal, etc.) are restored. Mirrors the Potion path's
    /// skip set — identity / canonical / dynamic-vars stay tied to the live orb.
    /// </summary>
    private static void CopyOrbMutableFields(OrbModel from, OrbModel to)
    {
        var skip = new HashSet<string>
        {
            "_canonicalInstance", "_owner",
            "<Id>k__BackingField", "<IsMutable>k__BackingField",
            "<CategorySortingId>k__BackingField", "<EntrySortingId>k__BackingField",
            "_dynamicVars",
        };
        for (var t = from.GetType(); t != null && t != typeof(object); t = t.BaseType)
        {
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic
                                          | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (f.IsLiteral || f.IsInitOnly) continue;
                if (skip.Contains(f.Name)) continue;
                try { f.SetValue(to, f.GetValue(from)); } catch { }
            }
        }
    }

    private static void RestorePotions(CombatSnapshot snap, CombatState cs)
    {
        foreach (var ally in cs.Allies)
        {
            var player = ally.Player;
            if (player == null) continue;

            var slotsObj = ReflectionCache.PlayerPotionSlotsField.GetValue(player);
            if (slotsObj == null) return;

            int restored = 0;
            int slotCount = 0;

            // Path A: array storage.
            if (slotsObj is Array arr)
            {
                int n = Math.Min(arr.Length, snap.PotionSlotRefs.Count);
                slotCount = arr.Length;
                for (int i = 0; i < n; i++)
                {
                    var savedRef = snap.PotionSlotRefs[i];
                    arr.SetValue(savedRef, i);
                    if (savedRef != null && snap.PotionClones.TryGetValue(savedRef, out var clone))
                        CopyPotionMutableFields(clone, savedRef, player);
                    restored++;
                }
            }
            // Path B: List<PotionModel?> or any IList-implementing storage.
            else if (slotsObj is System.Collections.IList list)
            {
                int n = Math.Min(list.Count, snap.PotionSlotRefs.Count);
                slotCount = list.Count;
                for (int i = 0; i < n; i++)
                {
                    var savedRef = snap.PotionSlotRefs[i];
                    list[i] = savedRef;
                    if (savedRef != null && snap.PotionClones.TryGetValue(savedRef, out var clone))
                        CopyPotionMutableFields(clone, savedRef, player);
                    restored++;
                }
            }
            else
            {
                RestoreLogger.Warn($"[Potions] _potionSlots is unexpected type {slotsObj.GetType().FullName}");
                return;
            }

            RestoreLogger.Info($"[Potions] restored {restored} of {snap.PotionSlotRefs.Count} saved (slot capacity={slotCount})");
            return;
        }
    }

    private static void CopyPotionMutableFields(PotionModel from, PotionModel to, Player owner)
    {
        var skip = new HashSet<string>
        {
            "_canonicalInstance", "_owner",
            "<Id>k__BackingField", "<IsMutable>k__BackingField",
            "<CategorySortingId>k__BackingField", "<EntrySortingId>k__BackingField",
            "_dynamicVars",
        };
        for (var t = typeof(PotionModel); t != null && t != typeof(object); t = t.BaseType)
        {
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic
                                          | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (f.IsLiteral || f.IsInitOnly) continue;
                if (skip.Contains(f.Name)) continue;
                try { f.SetValue(to, f.GetValue(from)); } catch { }
            }
        }
        // Restore _owner pointing to the live player (not the clone's owner).
        ReflectionCache.PotionOwnerField?.SetValue(to, owner);
    }

    private static void RestoreRunRng(CombatSnapshot snap, RunState runState)
    {
        var rngSet = runState.Rng;
        if (rngSet == null) return;
        if (ReflectionCache.RunRngDictField.GetValue(rngSet)
            is not Dictionary<RunRngType, Rng> dict) return;

        foreach (var (key, serializable) in snap.RunRngs)
            dict[key] = new Rng(serializable);
    }

    private static void RestoreHistory(CombatSnapshot snap, CombatManager cm)
    {
        if (snap.HistoryEntries == null) return;
        var history = ReflectionCache.CmHistoryProperty?.GetValue(cm);
        if (history == null) return;
        if (ReflectionCache.HistoryEntriesField?.GetValue(history)
            is not System.Collections.IList live) return;

        live.Clear();
        foreach (var e in snap.HistoryEntries) live.Add(e);
    }

    private static void TrySetProperty(object target, string name, object value)
    {
        var prop = AccessTools.Property(target.GetType(), name);
        if (prop?.CanWrite == true) { prop.SetValue(target, value); return; }
        // Fallback to backing field.
        var field = AccessTools.Field(target.GetType(), $"<{name}>k__BackingField");
        field?.SetValue(target, value);
    }
}
