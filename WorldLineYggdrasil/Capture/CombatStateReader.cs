using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using WorldLineYggdrasil.Graph;
using WorldLineYggdrasil.Restore;

namespace WorldLineYggdrasil.Capture;

/// <summary>
/// Reads the live game's combat state into a serializable <see cref="CombatSnapshotData"/>.
/// RNG is intentionally not read here (semantic node identity).
/// </summary>
public static class CombatStateReader
{
    public static CombatSnapshotData? Capture()
    {
        var combat = CombatManager.Instance.DebugOnlyGetState();
        if (combat == null || !combat.IsLiveCombat())
        {
            return null;
        }
        var player = LocalContext.GetMe(combat);
        if (player == null)
        {
            return null;
        }
        var pcs = player.PlayerCombatState;
        if (pcs == null)
        {
            return null;
        }

        var d = new CombatSnapshotData
        {
            RoundNumber = combat.RoundNumber,
            TurnNumber = pcs.TurnNumber,
            Side = combat.CurrentSide.ToString(),
            PlayerHp = player.Creature.CurrentHp,
            PlayerMaxHp = player.Creature.MaxHp,
            PlayerBlock = player.Creature.Block,
            Energy = pcs.Energy,
            Stars = pcs.Stars,
            Gold = player.Gold,
            MaxEnergy = pcs.MaxEnergy,
            Hand = Ids(pcs.Hand),
            Draw = Ids(pcs.DrawPile),
            Discard = Ids(pcs.DiscardPile),
            Exhaust = Ids(pcs.ExhaustPile),
            Play = Ids(pcs.PlayPile),
            PlayerPowers = Powers(player.Creature),
            Relics = player.Relics.Select(r => r.Id.Entry).ToArray(),
            RelicCounters = CaptureRelicCounters(player),
            Potions = player.Potions.Select(p => p.Id.Entry).ToArray(),
        };

        CaptureRngAndExtras(d, combat, player);

        foreach (var e in combat.Enemies)
        {
            var ed = new EnemyData
            {
                Id = e.Monster?.Id.Entry ?? "?",
                Hp = e.CurrentHp,
                Block = e.Block,
                Intent = e.Monster?.NextMove?.Id ?? "",
                Powers = Powers(e),
            };
            if (e.Monster != null)
            {
                if (ReflectionCache.MonsterRngField?.GetValue(e.Monster) is Rng mrng)
                {
                    ed.MonsterRng = mrng.ToSerializable();
                }
                CaptureMonsterMove(ed, e.Monster);
            }
            d.Enemies.Add(ed);
        }

        var run = RunManager.Instance.DebugOnlyGetState();
        d.CombatScope = $"{combat.Encounter?.Id.Entry ?? "?"}";
        return d;
    }

    /// <summary>Captures the RNG queues, potion slots and orbs needed to resume a
    /// combat step across a run reload. Best-effort: failures are non-fatal.</summary>
    private static void CaptureRngAndExtras(CombatSnapshotData d, CombatState combat, Player player)
    {
        try
        {
            var run = RunManager.Instance.DebugOnlyGetState();
            if (run?.Rng != null &&
                ReflectionCache.RunRngDictField.GetValue(run.Rng) is Dictionary<RunRngType, Rng> runDict)
            {
                foreach (var kv in runDict)
                {
                    d.RunRngs[kv.Key.ToString()] = kv.Value.ToSerializable();
                }
            }
        }
        catch (Exception ex)
        {
            RestoreLogger.Warn($"[Capture] run RNG failed: {ex.Message}");
        }

        try
        {
            if (player.PlayerRng != null)
            {
                foreach (var t in System.Enum.GetValues<PlayerRngType>())
                {
                    try
                    {
                        var rng = player.PlayerRng.GetRng(t);
                        if (rng != null)
                        {
                            d.PlayerRngs[t.ToString()] = rng.ToSerializable();
                        }
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            RestoreLogger.Warn($"[Capture] player RNG failed: {ex.Message}");
        }

        try
        {
            foreach (var slot in player.PotionSlots)
            {
                d.PotionSlots.Add(slot?.Id.Entry);
            }
        }
        catch (Exception ex)
        {
            RestoreLogger.Warn($"[Capture] potion slots failed: {ex.Message}");
        }

        try
        {
            if (player.PlayerCombatState?.OrbQueue != null)
            {
                foreach (var orb in player.PlayerCombatState.OrbQueue.Orbs)
                {
                    d.Orbs.Add(orb.Id.Entry);
                }
            }
        }
        catch (Exception ex)
        {
            RestoreLogger.Warn($"[Capture] orbs failed: {ex.Message}");
        }
    }

    private static void CaptureMonsterMove(EnemyData d, MegaCrit.Sts2.Core.Models.MonsterModel monster)
    {
        try
        {
            var sm = monster.MoveStateMachine;
            if (sm == null)
            {
                return;
            }
            d.PerformedFirstMove = (bool)(ReflectionCache.SmPerformedFirstMoveField?.GetValue(sm) ?? false);
            d.SpawnedThisTurn = (bool)(ReflectionCache.MonsterSpawnedField?.GetValue(monster) ?? false);
            var cur = ReflectionCache.SmCurrentStateField?.GetValue(sm);
            d.CurrentStateId = cur != null ? ReflectionCache.MonsterStateIdProperty?.GetValue(cur) as string : null;
            if (ReflectionCache.SmStateLogProp?.GetValue(sm) is System.Collections.IEnumerable sl)
            {
                foreach (var s in sl)
                {
                    if (s != null && ReflectionCache.MonsterStateIdProperty?.GetValue(s) is string id)
                    {
                        d.StateLogIds.Add(id);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            RestoreLogger.Warn($"[Capture] monster move failed: {ex.Message}");
        }
    }

    private static Dictionary<string, int> CaptureRelicCounters(Player player)
    {
        // "遗物计数" = progress counters on counter relics (Nunchaku, Happy
        // Flower, ...), which carry to the next combat. DisplayAmount is the
        // shown progress toward the next proc.
        var dict = new Dictionary<string, int>();
        foreach (var r in player.Relics)
        {
            try
            {
                dict[r.Id.Entry] = r.ShowCounter ? r.DisplayAmount : 0;
            }
            catch
            {
                dict[r.Id.Entry] = 0;
            }
        }
        return dict;
    }

    private static string[] Ids(CardPile pile) => pile.Cards.Select(c => c.Id.Entry).ToArray();

    private static Dictionary<string, int> Powers(Creature c)
    {
        var dict = new Dictionary<string, int>();
        foreach (var p in c.Powers)
        {
            dict[p.Id.Entry] = p.Amount;
        }
        return dict;
    }
}