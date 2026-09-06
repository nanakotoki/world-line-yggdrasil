using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.MonsterMoves;
using MegaCrit.Sts2.Core.Random;

namespace WorldLineYggdrasil.Simulation;

/// <summary>
/// Deterministic combat simulation: enumerates legal player actions from a
/// state and applies them (card plays, end turn -> enemy turn -> next turn,
/// potions). Powers/statuses use suffix-tolerant id matching so both
/// "VULNERABLE" and "VULNERABLE_POWER" style ids work.
/// </summary>
public static class SimEngine
{
    private const int HandSize = 5;

    /// <summary>Card lookup; replaced with a combat-aware resolver by auto-search.</summary>
    public static Func<string, SimCard?> CardResolver = id => CardDatabase.Get(id);

    /// <summary>Enemy definition (move pattern) lookup; replaced by runtime extraction.</summary>
    public static Func<string, SimEnemyDef?> EnemyDefResolver = id => EnemyDatabase.GetEnemy(id);

    /// <summary>Move effect lookup; replaced by runtime extraction.</summary>
    public static Func<string, string, SimMove?> EnemyMoveResolver =
        (enemyId, moveId) => EnemyDatabase.GetMove(enemyId, moveId);

    private static SimCard? GetCard(string id) => CardResolver(id);

    public static bool IsTerminal(SimState s) => !s.PlayerAlive || !s.EnemiesAlive;

    public static string TerminalKind(SimState s)
    {
        if (!s.PlayerAlive) return "Defeat";
        if (!s.EnemiesAlive) return "Victory";
        return "Ended";
    }

    public static List<SimAction> EnumActions(SimState s)
    {
        var actions = new List<SimAction>();
        if (IsTerminal(s))
        {
            return actions;
        }
        int enemies = s.Enemies.Count(e => e.Hp > 0);
        for (int i = 0; i < s.Hand.Count; i++)
        {
            var card = GetCard(s.Hand[i]);
            if (card == null || card.IsStatus || card.Cost > s.Energy)
            {
                continue;
            }
            if (card.NeedsTarget)
            {
                for (int t = 0; t < s.Enemies.Count; t++)
                {
                    if (s.Enemies[t].Hp > 0)
                    {
                        actions.Add(new SimAction { Kind = SimActionKind.PlayCard, CardIndex = i, TargetIndex = t });
                    }
                }
            }
            else
            {
                actions.Add(new SimAction { Kind = SimActionKind.PlayCard, CardIndex = i, TargetIndex = -1 });
            }
        }
        actions.Add(new SimAction { Kind = SimActionKind.EndTurn });
        for (int i = 0; i < s.Potions.Count; i++)
        {
            actions.Add(new SimAction { Kind = SimActionKind.DiscardPotion, CardIndex = i });
        }
        return actions;
    }

    /// <summary>Applies one player action; returns the new decision-point state.</summary>
    public static void Apply(SimState s, SimAction a)
    {
        if (IsTerminal(s))
        {
            return;
        }
        switch (a.Kind)
        {
            case SimActionKind.PlayCard:
                PlayCard(s, a);
                break;
            case SimActionKind.EndTurn:
                EndTurn(s);
                break;
            case SimActionKind.UsePotion:
                UsePotion(s, a);
                break;
                case SimActionKind.DiscardPotion:
                if (a.CardIndex >= 0 && a.CardIndex < s.Potions.Count)
                {
                    s.Potions.RemoveAt(a.CardIndex);
                    s.PotionsDiscarded++;
                }
                break;
        }
        CleanupDeadEnemies(s);
    }

    private static void PlayCard(SimState s, SimAction a)
    {
        if (a.CardIndex < 0 || a.CardIndex >= s.Hand.Count)
        {
            return;
        }
        var card = GetCard(s.Hand[a.CardIndex]);
        if (card == null || card.Cost > s.Energy)
        {
            return;
        }
        s.Energy -= card.Cost;

        var target = a.TargetIndex >= 0 && a.TargetIndex < s.Enemies.Count ? s.Enemies[a.TargetIndex] : null;

        int damageDealt = 0;
        foreach (var eff in card.Effects)
        {
            switch (eff.Type)
            {
                case SimEffectType.Attack:
                    if (target != null)
                    {
                        damageDealt += DealPlayerDamage(s, target, eff.Amount);
                    }
                    else
                    {
                        // cleave-style: hit all alive enemies
                        foreach (var e in s.Enemies.Where(e => e.Hp > 0))
                        {
                            damageDealt += DealPlayerDamage(s, e, eff.Amount);
                        }
                    }
                    break;
                case SimEffectType.Block:
                    int blk = eff.Amount;
                    if (GetPower(s.Powers, "FRAIL") > 0)
                    {
                        blk = (int)(blk * 0.75);
                    }
                    s.Block += blk;
                    break;
                case SimEffectType.Draw:
                    DrawCards(s, eff.Amount);
                    break;
                case SimEffectType.GainEnergy:
                    s.Energy += eff.Amount;
                    break;
                case SimEffectType.ApplyPowerToTarget:
                    if (target != null)
                    {
                        AddPower(target.Powers, eff.PowerId, eff.Amount);
                    }
                    break;
                case SimEffectType.ApplyPowerToSelf:
                    AddPower(s.Powers, eff.PowerId, eff.Amount);
                    break;
                case SimEffectType.AddStatusToDraw:
                    s.Draw.Add(eff.PowerId);
                    break;
                case SimEffectType.LoseHp:
                    // HP costs (Offering) can never kill the player.
                    s.Hp = Math.Max(1, s.Hp - eff.Amount);
                    break;
            }
        }

        if (card.BlockEqualsDamageDealt && damageDealt > 0)
        {
            s.Block += damageDealt;
        }

        // move the card out of hand
        string cardId = s.Hand[a.CardIndex];
        s.Hand.RemoveAt(a.CardIndex);
        if (card.IsEthereal || card.IsExhaust)
        {
            s.Exhaust.Add(cardId);
        }
        else
        {
            s.Discard.Add(cardId);
        }
    }

    private static void EndTurn(SimState s)
    {
        // discard hand
        s.Discard.AddRange(s.Hand);
        s.Hand.Clear();

        // Debuffs applied to the player during the enemy turn carry
        // SkipNextDurationTick in the real game, so they survive the next
        // player turn; only statuses that already existed before the enemy
        // turn decay at the player turn start.
        var preEnemyPlayerPowers = new HashSet<string>(s.Powers.Keys);

        // enemy turn: poison ticks and statuses decay at each enemy's turn start
        foreach (var e in s.Enemies.Where(e => e.Hp > 0))
        {
            TickPoison(e);
            DecayStatuses(e.Powers);
            EnemyAct(s, e);
            if (!s.PlayerAlive)
            {
                return;
            }
        }

        // start next player turn: poison ticks, only pre-existing player statuses decay
        s.RoundNumber++;
        s.TurnNumber++;
        s.Block = 0;
        s.Energy = s.MaxEnergy;
        TickPoison(s);
        DecayStatuses(s.Powers, preEnemyPlayerPowers);
        DrawCards(s, HandSize);
    }

    /// <summary>When set, each simulated enemy action is logged for 对拍 diagnosis.</summary>
    public static bool EnemyDiagnostics;

    private static void EnemyAct(SimState s, SimEnemy e)
    {
        if (EnemyDiagnostics)
        {
            var dbg = EnemyDefResolver(e.Id);
            var pat = dbg?.Moves != null ? string.Join(",", dbg.Moves) : "?";
            Log.Info($"[WorldLineYggdrasil.Sim] enemy {e.Id} acts {e.Intent} (idx={e.MoveIndex} pattern=[{pat}])");
        }
        var move = EnemyMoveResolver(e.Id, e.Intent);
        if (move == null)
        {
            return;
        }
        if (move.Block > 0)
        {
            e.Block += move.Block;
        }
        if (move.HealSelf > 0)
        {
            e.Hp = Math.Min(e.Hp + move.HealSelf, 999);
        }
        if (move.Damage > 0)
        {
            int dmg = move.Damage + GetPower(e.Powers, "STRENGTH");
            if (GetPower(e.Powers, "WEAK") > 0)
            {
                dmg = (int)(dmg * 0.75);
            }
            if (GetPower(s.Powers, "VULNERABLE") > 0)
            {
                dmg = (int)(dmg * 1.5);
            }
            dmg = Math.Max(dmg, 1);
            // block absorbs
            int absorbed = Math.Min(s.Block, dmg);
            s.Block -= absorbed;
            dmg -= absorbed;
            if (dmg > 0)
            {
                s.Hp = Math.Max(0, s.Hp - dmg);
            }
        }
        if (move.ApplyPowerToSelf != "")
        {
            AddPower(e.Powers, move.ApplyPowerToSelf, move.ApplyPowerToSelfAmount);
        }
        if (move.ApplyPowerToPlayer != "")
        {
            AddPower(s.Powers, move.ApplyPowerToPlayer, move.ApplyPowerToPlayerAmount);
        }
        if (move.StatusToPlayerDraw != "")
        {
            for (int i = 0; i < Math.Max(1, move.StatusCount); i++)
            {
                s.Discard.Add(move.StatusToPlayerDraw);
            }
        }
        // advance move pattern (follow the state machine graph, not a ring)
        var def = EnemyDefResolver(e.Id);
        if (def != null && def.Moves.Count > 0)
        {
            e.RecentMoves.Add(e.Intent);
            if (e.RecentMoves.Count > 8)
            {
                e.RecentMoves.RemoveAt(0);
            }
            if (def.Branches != null && def.Branches.TryGetValue(e.Intent, out var branches) && branches.Count > 0)
            {
                var next = RollBranch(branches, e.RecentMoves, s.MonsterRng);
                if (next != null)
                {
                    e.MoveIndex = def.Moves.IndexOf(next);
                    e.Intent = next;
                }
            }
            else if (def.NextIndex != null && e.MoveIndex >= 0 && e.MoveIndex < def.NextIndex.Length)
            {
                e.MoveIndex = def.NextIndex[e.MoveIndex];
                e.Intent = def.Moves[e.MoveIndex];
            }
            else
            {
                e.MoveIndex = (e.MoveIndex + 1) % def.Moves.Count;
                e.Intent = def.Moves[e.MoveIndex];
            }
        }
    }

    private static void UsePotion(SimState s, SimAction a)
    {
        // v1: potion effects not modeled; just consume a basic heal/fire potion
        if (a.CardIndex < 0 || a.CardIndex >= s.Potions.Count)
        {
            return;
        }
        s.Potions.RemoveAt(a.CardIndex);
        s.PotionsUsed++;
    }

    private static int DealPlayerDamage(SimState s, SimEnemy target, int baseDmg)
    {
        int dmg = baseDmg + GetPower(s.Powers, "STRENGTH");
        if (GetPower(s.Powers, "WEAK") > 0)
        {
            dmg = (int)(dmg * 0.75);
        }
        if (GetPower(s.Powers, "SHRINK") > 0)
        {
            dmg = (int)(dmg * 0.7);
        }
        if (GetPower(target.Powers, "VULNERABLE") > 0)
        {
            dmg = (int)(dmg * 1.5);
        }
        if (EnemyDiagnostics)
        {
            Log.Info($"[WorldLineYggdrasil.Sim] deal {baseDmg}->{dmg} to {target.Id} (vuln={GetPower(target.Powers, "VULNERABLE")} pwr=[{string.Join(",", target.Powers.Select(kv => $"{kv.Key}:{kv.Value}"))}])");
        }
        dmg = Math.Max(dmg, 1);
        int absorbed = Math.Min(target.Block, dmg);
        target.Block -= absorbed;
        dmg -= absorbed;
        if (dmg > 0)
        {
            target.Hp = Math.Max(0, target.Hp - dmg);
        }
        return absorbed + dmg;
    }

    private static void DrawCards(SimState s, int n)
    {
        for (int i = 0; i < n; i++)
        {
            if (s.Draw.Count == 0)
            {
                if (s.Discard.Count == 0)
                {
                    return;
                }
                s.Draw.AddRange(s.Discard);
                s.Discard.Clear();
                Reshuffle(s.Draw, s);
            }
            string card = s.Draw[0];
            s.Draw.RemoveAt(0);
            s.Hand.Add(card);
        }
    }

    /// <summary>Reproduces the game's StableShuffle (sort then Fisher-Yates) with
    /// the captured Shuffle RNG when available, else a simple shuffle.</summary>
    private static void Reshuffle(List<string> list, SimState s)
    {
        if (s.ShuffleRng != null)
        {
            // StableShuffle: copy + sort, then Fisher-Yates with rng.NextInt(n+1)
            var sorted = list.OrderBy(x => x, System.StringComparer.Ordinal).ToList();
            for (int i = 0; i < list.Count; i++)
            {
                list[i] = sorted[i];
            }
            int num = list.Count;
            while (num > 1)
            {
                num--;
                int j = s.ShuffleRng.Next(num + 1);
                (list[num], list[j]) = (list[j], list[num]);
            }
        }
        else
        {
            Shuffle(list, s.Rng);
        }
    }

    private static void Shuffle(List<string> list, System.Random rng)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>Rolls the next random-branch move with the captured MonsterAi RNG,
    /// mirroring RandomBranchState.GetNextState (weights + repeat rules).</summary>
    private static string? RollBranch(List<SimBranch> branches, List<string> recent, MegaRandom? rng)
    {
        float total = 0f;
        var eligible = new List<(SimBranch b, float w)>();
        foreach (var b in branches)
        {
            float w = BranchWeight(b, recent);
            if (w > 0f)
            {
                total += w;
                eligible.Add((b, w));
            }
        }
        if (eligible.Count == 0)
        {
            return null;
        }
        if (rng == null)
        {
            return eligible[0].b.NextId; // deterministic fallback
        }
        float roll = rng.NextFloat() * total;
        foreach (var (b, w) in eligible)
        {
            roll -= w;
            if (roll <= 0f)
            {
                return b.NextId;
            }
        }
        return eligible[eligible.Count - 1].b.NextId;
    }

    private static float BranchWeight(SimBranch b, List<string> recent)
    {
        switch (b.RepeatType)
        {
            case MoveRepeatType.UseOnlyOnce:
                return recent.Contains(b.NextId) ? 0f : 1f;
            case MoveRepeatType.CannotRepeat:
                return recent.Count > 0 && recent[recent.Count - 1] == b.NextId ? 0f : 1f;
            case MoveRepeatType.CanRepeatXTimes:
                int max = Math.Max(1, b.MaxRepeats);
                int count = 0;
                for (int i = recent.Count - 1; i >= 0 && count < max; i--, count++)
                {
                    if (recent[i] != b.NextId)
                    {
                        return 1f;
                    }
                }
                return count >= max ? 0f : 1f;
            default:
                return 1f; // CanRepeatForever
        }
    }

    private static void CleanupDeadEnemies(SimState s)
    {
        s.Enemies.RemoveAll(e => e.Hp <= 0);
    }

    private static void DecayStatuses(Dictionary<string, int> powers, HashSet<string>? preEnemy = null)
    {
        // VULNERABLE/WEAK/FRAIL are Counter debuffs (decay 1 per turn).
        // SHRINK is NOT here: monsters apply it with a negative amount
        // (ShrinkPower -1m => IsInfinite), so it persists for the whole fight
        // (removed only when the applier dies, which ends the combat).
        foreach (var key in new[] { "VULNERABLE", "WEAK", "FRAIL" })
        {
            var found = FindKey(powers, key);
            if (found == null || powers[found] <= 0)
            {
                continue;
            }
            // skip newly-applied statuses (SkipNextDurationTick equivalent)
            if (preEnemy != null && !preEnemy.Contains(found))
            {
                continue;
            }
            powers[found]--;
            if (powers[found] <= 0)
            {
                powers.Remove(found);
            }
        }
    }

    /// <summary>Poison ticks at the start of the affected creature's turn
    /// (HP loss equal to stacks), then the stacks decay by one.</summary>
    private static void TickPoison(SimState s)
    {
        var p = GetPower(s.Powers, "POISON");
        if (p <= 0)
        {
            return;
        }
        s.Hp = Math.Max(0, s.Hp - p);
        var key = FindKey(s.Powers, "POISON") ?? "POISON";
        if (s.Powers[key] > 1)
        {
            s.Powers[key]--;
        }
        else
        {
            s.Powers.Remove(key);
        }
    }

    private static void TickPoison(SimEnemy e)
    {
        var p = GetPower(e.Powers, "POISON");
        if (p <= 0)
        {
            return;
        }
        e.Hp = Math.Max(0, e.Hp - p);
        var key = FindKey(e.Powers, "POISON") ?? "POISON";
        if (e.Powers[key] > 1)
        {
            e.Powers[key]--;
        }
        else
        {
            e.Powers.Remove(key);
        }
    }

    private static string? FindKey(Dictionary<string, int> powers, string id)
    {
        if (powers.ContainsKey(id))
        {
            return id;
        }
        var suffixed = id + "_POWER";
        return powers.ContainsKey(suffixed) ? suffixed : null;
    }

    private static int GetPower(Dictionary<string, int> powers, string id)
    {
        return powers.GetValueOrDefault(id, powers.GetValueOrDefault(id + "_POWER", 0));
    }

    private static void AddPower(Dictionary<string, int> powers, string id, int amount)
    {
        var key = FindKey(powers, id) ?? id;
        powers[key] = powers.GetValueOrDefault(key) + amount;
    }
}