using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;

namespace WorldLineYggdrasil.Simulation;

/// <summary>
/// Reads enemy move data ON DEMAND from the LIVE combat: the resolver asks for
/// a specific enemy / move id and the data is extracted fresh from the monster's
/// move state machine (pattern + followup graph) and its attack intents
/// (DamageCalc = base damage). Buff/debuff/status/defend specific values are not
/// readable from the intent objects (they live inside the move delegates), so
/// those use a small per-move mapping that grows only as diffs discover them.
/// </summary>
public static class RuntimeEnemyData
{
    private static readonly Dictionary<string, SimEnemyDef> DefCache = new();
    private static readonly Dictionary<string, SimMove> MoveCache = new();

    public static void ClearCache()
    {
        DefCache.Clear();
        MoveCache.Clear();
    }

    public static SimEnemyDef? GetEnemyDef(string entryId)
    {
        if (DefCache.TryGetValue(entryId, out var def))
        {
            return def;
        }
        var monster = FindMonster(entryId);
        if (monster == null)
        {
            return null;
        }
        def = BuildDef(monster);
        if (def.Moves.Count > 0)
        {
            DefCache[entryId] = def;
        }
        return def;
    }

    public static SimMove? GetMove(string enemyId, string moveId)
    {
        var key = $"{enemyId}:{moveId}";
        if (MoveCache.TryGetValue(key, out var mv))
        {
            return mv;
        }
        var monster = FindMonster(enemyId);
        if (monster == null)
        {
            return null;
        }
        mv = BuildMove(monster, moveId);
        if (mv != null)
        {
            MoveCache[key] = mv;
        }
        return mv;
    }

    private static MonsterModel? FindMonster(string entryId)
    {
        var cs = CombatManager.Instance?.DebugOnlyGetState();
        if (cs == null)
        {
            return null;
        }
        foreach (var e in cs.Enemies)
        {
            if (e.Monster?.Id.Entry == entryId)
            {
                return e.Monster;
            }
        }
        return null;
    }

    private static SimEnemyDef BuildDef(MonsterModel monster)
    {
        var def = new SimEnemyDef { Id = monster.Id.Entry };
        var seen = new Dictionary<string, int>();
        var states = monster.MoveStateMachine?.States;
        if (states == null)
        {
            return def;
        }
        var cur = monster.NextMove;
        while (cur != null && !seen.ContainsKey(cur.Id))
        {
            seen[cur.Id] = def.Moves.Count;
            def.Moves.Add(cur.Id);
            cur = ResolveNextMove(states, cur);
        }
        if (def.Moves.Count > 0)
        {
            def.NextIndex = new int[def.Moves.Count];
            cur = monster.NextMove;
            for (int i = 0; i < def.Moves.Count; i++)
            {
                var nxt = ResolveNextMove(states, cur);
                def.NextIndex[i] = nxt != null && seen.TryGetValue(nxt.Id, out var ni)
                    ? ni
                    : (i + 1) % def.Moves.Count;
                cur = nxt;
            }
        }
        // random branches: capture the followup options with their repeat rules
        foreach (var move in def.Moves)
        {
            if (!states.TryGetValue(move, out var st) || st is not MoveState ms)
            {
                continue;
            }
            var nextId = ms.FollowUpState?.Id ?? ms.FollowUpStateId;
            if (nextId == null || !states.TryGetValue(nextId, out var ns) || ns is not RandomBranchState rb)
            {
                continue;
            }
            var list = new List<SimBranch>();
            foreach (var b in rb.States)
            {
                if (!states.TryGetValue(b.stateId, out var bs) || bs is not MoveState)
                {
                    continue;
                }
                list.Add(new SimBranch
                {
                    NextId = b.stateId,
                    RepeatType = b.repeatType,
                    MaxRepeats = b.maxTimes,
                });
            }
            if (list.Count > 0)
            {
                (def.Branches ??= new Dictionary<string, List<SimBranch>>())[move] = list;
            }
        }
        return def;
    }

    /// <summary>
    /// Follows the state machine from the current move to the next one.
    /// RandomBranchState branches are resolved deterministically when a
    /// CannotRepeat branch excludes the move just used (the common alternation
    /// pattern); otherwise the first eligible branch is used as a best guess.
    /// </summary>
    private static MoveState? ResolveNextMove(Dictionary<string, MonsterState> states, MoveState cur)
    {
        var nextId = cur.FollowUpState?.Id ?? cur.FollowUpStateId;
        if (nextId == null || !states.TryGetValue(nextId, out var ns))
        {
            return null;
        }
        if (ns is MoveState ms)
        {
            return ms;
        }
        if (ns is RandomBranchState rb)
        {
            MoveState? fallback = null;
            foreach (var b in rb.States)
            {
                if (!states.TryGetValue(b.stateId, out var bs) || bs is not MoveState bms)
                {
                    continue;
                }
                fallback ??= bms;
                // CannotRepeat excludes the move just used (weight 0) -> forced.
                if (b.repeatType == MoveRepeatType.CannotRepeat && bms.Id == cur.Id)
                {
                    continue;
                }
                return bms;
            }
            return fallback;
        }
        return null;
    }

    private static SimMove? BuildMove(MonsterModel monster, string moveId)
    {
        if (!monster.MoveStateMachine.States.TryGetValue(moveId, out var st) || st is not MoveState ms)
        {
            return null;
        }
        var sim = new SimMove { Id = moveId };

        foreach (var intent in ms.Intents)
        {
            switch (intent)
            {
                case AttackIntent attack:
                    try
                    {
                        // Base per-hit damage from DamageCalc; the simulator applies
                        // strength / weak / vulnerable itself. GetTotalDamage would
                        // double-count those modifiers.
                        double basePerHit = (double)(attack.DamageCalc?.Invoke() ?? 0m);
                        sim.Damage = Math.Max(sim.Damage, (int)(basePerHit * Math.Max(1, attack.Repeats)));
                    }
                    catch
                    {
                    }
                    break;
                case DefendIntent:
                    sim.Block = KnownBlock(moveId);
                    break;
                case StatusIntent stIntent:
                    // which status not readable; map known status moves
                    var status = KnownStatus(moveId);
                    if (status != "")
                    {
                        sim.StatusToPlayerDraw = status;
                        sim.StatusCount = Math.Max(1, stIntent.CardCount);
                    }
                    break;
                case DebuffIntent debuff:
                    // which debuff not readable; strong debuffs default to
                    // vulnerable, normal ones to weak, exceptions mapped by id
                    if (debuff.IntentType == IntentType.DebuffStrong)
                    {
                        sim.ApplyPowerToPlayer = "VULNERABLE";
                        sim.ApplyPowerToPlayerAmount = 2;
                    }
                    else
                    {
                        sim.ApplyPowerToPlayer = "WEAK";
                        sim.ApplyPowerToPlayerAmount = 2;
                    }
                    KnownDebuff(moveId, sim);
                    break;
                case BuffIntent:
                    var (pow, amt) = KnownBuff(moveId);
                    sim.ApplyPowerToSelf = pow;
                    sim.ApplyPowerToSelfAmount = amt;
                    break;
            }
        }
        return sim;
    }

    private static int KnownBlock(string moveId) => moveId switch
    {
        "SLICE_MOVE" => 5, // Nibbit: SliceBlock = 5 (6 on ToughEnemies)
        _ => 0, // extend here as defend moves are discovered by diffing
    };

    private static string KnownStatus(string moveId)
    {
        if (moveId.Contains("GOOP") || moveId.Contains("STICKY") || moveId.Contains("SPLAT"))
        {
            return "SLIMED";
        }
        return "";
    }

    private static void KnownDebuff(string moveId, SimMove sim)
    {
        // Power.tabx semantics: ShrinkerBeetle applies ShrinkPower
        // ("缩小": the creature's attack damage -30%), a counter that decays.
        if (moveId.Contains("SHRINKER"))
        {
            sim.ApplyPowerToPlayer = "SHRINK";
            sim.ApplyPowerToPlayerAmount = 1;
        }
    }

    private static (string power, int amount) KnownBuff(string moveId) => moveId switch
    {
        "INHALE" => ("STRENGTH", 7), // FuzzyWurmCrawler: PowerCmd.Apply<StrengthPower>(7)
        "HISS_MOVE" => ("STRENGTH", 2), // Nibbit: HissStrengthGain = 2
        _ => ("", 0),
    };
}