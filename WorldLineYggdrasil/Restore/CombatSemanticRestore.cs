using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using WorldLineYggdrasil.Graph;
using WorldLineYggdrasil.Restore.Visuals;
using WorldLineYggdrasil.Search;

namespace WorldLineYggdrasil.Restore;

/// <summary>
/// Best-effort "semantic" restore of a recorded combat state onto a live
/// combat. Used to jump to a specific small node after re-entering a past
/// combat via a big-node restore. Rebuilds the piles by matching card ids to
/// the fresh combat's card instances and reuses the undo-mod reflection cache
/// for player/creature values. Powers/RNG are not yet reproduced (v1).
/// </summary>
public static class CombatSemanticRestore
{
    /// <summary>
    /// Direct entry (no LoadRun): retries ApplyToLive each frame until the live
    /// combat has cards, then applies. Used when jumping between small nodes of a
    /// combat that is ALREADY live (same encounter), so we don't re-LoadRun and
    /// replay the opening deal animation every click.
    /// </summary>
    public static void BeginApply(CombatSnapshotData data, int attempt = 0)
    {
        SearchSpeed.SuppressCardTweens = true;
        Godot.Callable.From(() =>
        {
            if (!ReferenceEquals(RestoreService.PendingSemanticRestore, data))
            {
                return;
            }
            if (ApplyToLive(data))
            {
                RestoreService.ClearPendingSemanticRestore();
                RestoreLogger.Info($"[SemanticRestore] applied (attempt {attempt})");
            }
            else if (attempt < 90)
            {
                BeginApply(data, attempt + 1);
            }
            else
            {
                RestoreService.ClearPendingSemanticRestore();
                RestoreLogger.Warn($"[SemanticRestore] gave up after {attempt} attempts");
            }
        }).CallDeferred();
    }

    public static bool ApplyToLive(CombatSnapshotData d)
    {
        var cs = CombatManager.Instance.DebugOnlyGetState();
        if (cs == null || !cs.IsLiveCombat())
        {
            return false;
        }
        var player = LocalContext.GetMe(cs);
        if (player == null || player.PlayerCombatState == null)
        {
            return false;
        }
        var pcs = player.PlayerCombatState;

        // If the re-entered combat has not created its card instances yet, the
        // pile rebuild would match nothing and the hand would stay frozen at the
        // opening deal. Signal "not ready" so the caller retries next frame.
        if (pcs.AllPiles.Sum(p => p.Cards.Count) == 0)
        {
            return false;
        }

        try
        {
            // close any orphaned card-selection overlays (potion etc.) so they
            // don't survive the restore and allow double-use
            RestoreService.CancelOpenCardSelections();

            // round / turn
            SetPrivateProperty(cs, nameof(CombatState.RoundNumber), d.RoundNumber);
            if (ReflectionCache.PcsTurnNumberSetter is { } turnSetter && d.TurnNumber > 0)
            {
                turnSetter.Invoke(pcs, new object[] { d.TurnNumber });
            }

            // player body
            SetCreatureHpBlock(player.Creature, d.PlayerHp, d.PlayerMaxHp, d.PlayerBlock);
            pcs.Energy = Math.Max(0, d.Energy);
            pcs.Stars = Math.Max(0, d.Stars);
            if (ReflectionCache.PlayerGoldField is { } goldField)
            {
                goldField.SetValue(player, Math.Max(0, d.Gold));
            }

            // piles: rebuild all together with a shared claim set so cards are not
            // double-placed, non-silent ContentsChanged fires refresh the UI
            RebuildAllPiles(pcs, d);
            RestoreLogger.Info($"[SemanticRestore] applied: want hand=[{string.Join(",", d.Hand)}] | live hand=[{string.Join(",", pcs.Hand.Cards.Select(c => c.Id.Entry))}] draw={pcs.DrawPile.Cards.Count} disc={pcs.DiscardPile.Cards.Count} ex={pcs.ExhaustPile.Cards.Count}");

            // The model piles are correct now, but the rendered hand still shows
            // the opening deal unless we rebuild the visual hand from the live
            // card models (mirrors the deep-restore HandRefresher path).
            try
            {
                HandRefresher.RefreshFromLive(pcs.Hand.Cards.ToList());
            }
            catch (Exception ex)
            {
                RestoreLogger.Warn($"[SemanticRestore] hand visual refresh failed: {ex.Message}");
            }
            // draw/discard/exhaust pile count labels go stale after silent pile
            // edits (they update on CardAdd/Remove events) — refresh from live.
            try
            {
                PileCountRefresher.RefreshLive();
            }
            catch (Exception ex)
            {
                RestoreLogger.Warn($"[SemanticRestore] pile-count refresh failed: {ex.Message}");
            }

            // enemies by slot (order is stable inside a combat)
            var liveEnemies = cs.Enemies.ToList();
            for (int i = 0; i < d.Enemies.Count && i < liveEnemies.Count; i++)
            {
                var e = liveEnemies[i];
                var ed = d.Enemies[i];
                SetCreatureHpBlock(e, ed.Hp, Math.Max(e.MaxHp, ed.Hp), ed.Block);
            }

            // RNG alignment only: the other "full" restores (powers, monster
            // move-state, potion slots, orbs) desync the fresh combat in real play
            // (cards unplayable, enemy AI inconsistent) and are disabled until they
            // can be made safe. RNG is a pure data write and is what keeps a
            // different replay from diverging.
            RestoreRunRngs(d);

            // re-enable card VFX a couple frames after the state settles so the
            // hand/pile rebuilds during this apply stay animation-free.
            Godot.Callable.From(() =>
                Godot.Callable.From(() => SearchSpeed.SuppressCardTweens = false).CallDeferred()
            ).CallDeferred();

            return true;
        }
        catch (Exception ex)
        {
            RestoreLogger.Warn($"[SemanticRestore] failed: {ex}");
            return false;
        }
    }

    private static void RestorePowers(Creature creature, Dictionary<string, int> powers)
    {
        if (ReflectionCache.CreaturePowersField.GetValue(creature) is not System.Collections.IList list)
        {
            return;
        }
        try
        {
            // Remove existing powers WITHOUT firing PowerApplied/Removed events
            // (ApplyInternal/RemoveInternal fire those and corrupt the combat's
            // hook-listener list). The game's own undo path mutates _powers directly.
            list.Clear();

            foreach (var (id, amount) in powers)
            {
                if (amount == 0)
                {
                    continue;
                }
                var canonical = ModelDb.AllPowers.FirstOrDefault(p => p.Id.Entry == id);
                if (canonical == null)
                {
                    continue;
                }
                try
                {
                    var pm = canonical.ToMutable();
                    // _owner must be set before adding (public Owner setter throws on move)
                    ReflectionCache.PowerOwnerField?.SetValue(pm, creature);
                    ReflectionCache.PowerAmountField.SetValue(pm, amount);
                    list.Add(pm);
                }
                catch (Exception ex)
                {
                    RestoreLogger.Warn($"[SemanticRestore] power {id} failed: {ex.Message}");
                }
            }
            RestoreLogger.Info($"[SemanticRestore] powers set for {(creature.IsPlayer ? "player" : "enemy")}: [{string.Join(", ", powers.Where(kv => kv.Value != 0).Select(kv => $"{kv.Key}={kv.Value}"))}]");
        }
        catch (Exception ex)
        {
            RestoreLogger.Warn($"[SemanticRestore] powers failed: {ex.Message}");
        }
    }

    private static void RestoreRunRngs(CombatSnapshotData d)
    {
        try
        {
            var run = RunManager.Instance.DebugOnlyGetState();
            if (run?.Rng != null &&
                ReflectionCache.RunRngDictField.GetValue(run.Rng) is Dictionary<RunRngType, Rng> dict)
            {
                foreach (var (name, ser) in d.RunRngs)
                {
                    if (System.Enum.TryParse<RunRngType>(name, out var t))
                    {
                        dict[t] = new Rng(ser);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            RestoreLogger.Warn($"[SemanticRestore] run RNG restore failed: {ex.Message}");
        }

        try
        {
            var player = LocalContext.GetMe(CombatManager.Instance.DebugOnlyGetState());
            if (player?.PlayerRng != null)
            {
                var pr = player.PlayerRng;
                foreach (var (name, ser) in d.PlayerRngs)
                {
                    if (System.Enum.TryParse<PlayerRngType>(name, out var t))
                    {
                        var rng = pr.GetRng(t);
                        rng?.LoadFromSerializable(ser);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            RestoreLogger.Warn($"[SemanticRestore] player RNG restore failed: {ex.Message}");
        }
    }

    private static void RestoreMonsterState(CombatState cs, CombatSnapshotData d)
    {
        var liveEnemies = cs.Enemies.ToList();
        for (int i = 0; i < d.Enemies.Count && i < liveEnemies.Count; i++)
        {
            var e = liveEnemies[i];
            var ed = d.Enemies[i];
            var monster = e.Monster;
            if (monster == null)
            {
                continue;
            }
            try
            {
                if (ed.MonsterRng != null && ReflectionCache.MonsterRngField?.GetValue(monster) is Rng mrng)
                {
                    // write back in place: Rng has LoadFromSerializable
                    mrng.LoadFromSerializable(ed.MonsterRng);
                }
            }
            catch { }

            try
            {
                var sm = monster.MoveStateMachine;
                if (sm == null)
                {
                    continue;
                }
                ReflectionCache.SmPerformedFirstMoveField?.SetValue(sm, ed.PerformedFirstMove);
                ReflectionCache.MonsterSpawnedField?.SetValue(monster, ed.SpawnedThisTurn);

                if (ReflectionCache.SmStatesProp?.GetValue(sm) is System.Collections.IDictionary states)
                {
                    // current state by id
                    if (ed.CurrentStateId != null && states.Contains(ed.CurrentStateId))
                    {
                        var st = states[ed.CurrentStateId];
                        if (st != null)
                        {
                            try { ReflectionCache.SmForceCurrentStateMethod?.Invoke(sm, new[] { st }); }
                            catch { }
                        }
                    }
                    // state log by id
                    if (ReflectionCache.SmStateLogProp?.GetValue(sm) is System.Collections.IList log)
                    {
                        log.Clear();
                        foreach (var id in ed.StateLogIds)
                        {
                            if (states.Contains(id))
                            {
                                log.Add(states[id]!);
                            }
                        }
                    }
                    // NextMove by id (set via reflection; private setter)
                    if (ed.Intent != null && states.Contains(ed.Intent))
                    {
                        var ns = states[ed.Intent];
                        if (ns != null)
                        {
                            try { ReflectionCache.NextMoveProp?.SetValue(monster, ns); }
                            catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                RestoreLogger.Warn($"[SemanticRestore] monster {ed.Id} move restore failed: {ex.Message}");
            }
        }
    }

    private static void RestorePotionSlotsAndOrbs(Player player, CombatSnapshotData d)
    {
        try
        {
            if (d.PotionSlots.Count > 0 && ReflectionCache.PlayerPotionSlotsField.GetValue(player) is System.Collections.IList slots)
            {
                for (int i = 0; i < slots.Count && i < d.PotionSlots.Count; i++)
                {
                    var want = d.PotionSlots[i];
                    if (want == null)
                    {
                        continue; // empty slot: leave as-is (best effort)
                    }
                    var cur = slots[i] as PotionModel;
                    if (cur == null || cur.Id.Entry != want)
                    {
                        var pm = ModelDb.AllPotions.FirstOrDefault(p => p.Id.Entry == want)?.ToMutable();
                        if (pm != null)
                        {
                            try { slots[i] = pm; }
                            catch { }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            RestoreLogger.Warn($"[SemanticRestore] potion slots failed: {ex.Message}");
        }

        try
        {
            if (d.Orbs.Count > 0 && player.PlayerCombatState?.OrbQueue != null)
            {
                var q = player.PlayerCombatState.OrbQueue;
                var orbsField = HarmonyLib.AccessTools.Field(q.GetType(), "_orbs");
                if (orbsField?.GetValue(q) is System.Collections.IList orbList)
                {
                    orbList.Clear();
                    foreach (var id in d.Orbs)
                    {
                        var orb = ModelDb.Orbs.FirstOrDefault(o => o.Id.Entry == id)?.ToMutable();
                        if (orb != null)
                        {
                            orbList.Add(orb);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            RestoreLogger.Warn($"[SemanticRestore] orbs failed: {ex.Message}");
        }
    }

    private static void SetCreatureHpBlock(Creature c, int hp, int maxHp, int block)
    {
        int oldHp = (int)(ReflectionCache.CreatureHpField.GetValue(c) ?? 0);
        int oldMax = (int)(ReflectionCache.CreatureMaxHpField.GetValue(c) ?? 0);
        int oldBlock = (int)(ReflectionCache.CreatureBlockField.GetValue(c) ?? 0);

        ReflectionCache.CreatureHpField.SetValue(c, Math.Max(0, hp));
        ReflectionCache.CreatureMaxHpField.SetValue(c, Math.Max(1, maxHp));
        ReflectionCache.CreatureBlockField.SetValue(c, Math.Max(0, block));

        Fire(c, ReflectionCache.CreatureCurrentHpChangedField, oldHp, Math.Max(0, hp));
        Fire(c, ReflectionCache.CreatureMaxHpChangedField, oldMax, Math.Max(1, maxHp));
        Fire(c, ReflectionCache.CreatureBlockChangedField, oldBlock, Math.Max(0, block));
    }

    private static void Fire(object target, System.Reflection.FieldInfo? field, params object?[] args)
    {
        if (field == null)
        {
            return;
        }
        try
        {
            if (field.GetValue(target) is Delegate d)
            {
                d.DynamicInvoke(args);
            }
        }
        catch (Exception ex)
        {
            RestoreLogger.Warn($"[SemanticRestore] event fire {field.Name} failed: {ex.Message}");
        }
    }

    private static void RebuildAllPiles(PlayerCombatState pcs, CombatSnapshotData d)
    {
        var pileTargets = new (CardPile pile, string[] ids)[]
        {
            (pcs.Hand, d.Hand),
            (pcs.DrawPile, d.Draw),
            (pcs.DiscardPile, d.Discard),
            (pcs.ExhaustPile, d.Exhaust),
            (pcs.PlayPile, d.Play),
        };

        var allCards = pcs.AllPiles.SelectMany(p => p.Cards).ToList();
        var claimed = new HashSet<CardModel>(ReferenceEqualityComparer.Instance);

        foreach (var (pile, ids) in pileTargets)
        {
            foreach (var id in ids)
            {
                var match = allCards.FirstOrDefault(c => !claimed.Contains(c) && c.Id.Entry == id);
                if (match == null)
                {
                    continue;
                }
                claimed.Add(match);
                if (match.Pile != null)
                {
                    try { match.Pile.RemoveInternal(match, silent: true); } catch { }
                }
                pile.AddInternal(match, silent: true);
            }
        }

        // remove leftovers (instances present in the live combat but absent at
        // the recorded node, e.g. cards already exhausted there)
        foreach (var c in pcs.AllPiles.SelectMany(p => p.Cards).Where(c => !claimed.Contains(c)).ToList())
        {
            if (c.Pile != null)
            {
                try { c.Pile.RemoveInternal(c, silent: true); } catch { }
            }
        }

        foreach (var (pile, _) in pileTargets)
        {
            pile.InvokeContentsChanged();
        }
    }

    private static void SetPrivateProperty(object target, string name, object value)
    {
        var setter = AccessTools.PropertySetter(target.GetType(), name);
        if (setter != null)
        {
            setter.Invoke(target, new[] { value });
        }
    }
}