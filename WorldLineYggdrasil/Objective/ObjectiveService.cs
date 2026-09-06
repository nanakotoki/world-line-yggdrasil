using WorldLineYggdrasil.Graph;

namespace WorldLineYggdrasil.Objective;

/// <summary>
/// The out-of-combat gains of a combat outcome (relative to the combat-start
/// root state). A lower score is "worse" for things we lose (HP) and better
/// for things we gain (gold, potions, max HP, relic progress).
/// </summary>
public sealed class Outcome
{
    public int HpLost { get; set; }
    public int GoldDelta { get; set; }
    public int PotionDelta { get; set; }
    public int MaxHpDelta { get; set; }
    public int RelicCounterDelta { get; set; }
    public int CardDelta { get; set; }

    /// <summary>Weighted scalar, higher = better.</summary>
    public double Score { get; set; }

    public string Summary()
    {
        return $"战损-{HpLost} 金币{GoldDelta:+0;-0;0} 药水{PotionDelta:+0;-0;0} 卡牌{CardDelta:+0;-0;0} 生命上限{MaxHpDelta:+0;-0;0} 遗物计数{RelicCounterDelta:+0;-0;0}";
    }
}

/// <summary>
/// Player preference weights. Higher weight = the player values that factor
/// more. HpLost is inverted (we want to LOSE the least HP).
/// </summary>
public sealed class ObjectiveWeights
{
    public double HpLost { get; set; } = 1.0;
    public double Gold { get; set; } = 0.5;
    public double Potion { get; set; } = 0.5;
    public double MaxHp { get; set; } = 1.0;
    public double Relic { get; set; } = 0.5;
    public double Card { get; set; } = 0.5;
}

public static class ObjectiveService
{
    /// <summary>
    /// Fixed penalty for a loss, applied AFTER the weighted score so that a
    /// defeat always ranks far below any victory — even on act-3 bosses where the
    /// HpLost weight is zero (otherwise a loss could score ~0 and tie/beat a win).
    /// </summary>
    public const double DefeatPenalty = 1000.0;

    public static Outcome ComputeOutcome(GraphNode root, GraphNode terminal, int bossLevel)
    {
        bool isDefeat = terminal.TerminalKind == "Defeat" || terminal.Snapshot.PlayerHp <= 0;
        return ComputeOutcome(root.Snapshot, terminal.Snapshot, bossLevel, isDefeat);
    }

    public static Outcome ComputeOutcome(CombatSnapshotData r, CombatSnapshotData t, int bossLevel)
    {
        return ComputeOutcome(r, t, bossLevel, t.PlayerHp <= 0);
    }

    private static Outcome ComputeOutcome(CombatSnapshotData r, CombatSnapshotData t, int bossLevel, bool isDefeat)
    {
        var outcome = new Outcome
        {
            HpLost = Math.Max(0, r.PlayerHp - t.PlayerHp),
            GoldDelta = t.GoldGained - r.GoldGained,
            // generated potions are a gain, used/discarded are a cost
            PotionDelta = (t.PotionsGenerated - t.PotionsUsed - t.PotionsDiscarded)
                        - (r.PotionsGenerated - r.PotionsUsed - r.PotionsDiscarded),
            MaxHpDelta = t.PlayerMaxHp - r.PlayerMaxHp,
            CardDelta = t.CardsGained - r.CardsGained,
        };

        // relic progress proxy: sum of StackCount deltas across shared relics
        int relicDelta = 0;
        foreach (var (relicId, rootCount) in r.RelicCounters)
        {
            if (t.RelicCounters.TryGetValue(relicId, out int termCount))
            {
                relicDelta += termCount - rootCount;
            }
        }
        outcome.RelicCounterDelta = relicDelta;

        outcome.Score = WeightedScore(outcome, CurrentWeights, bossLevel);
        if (isDefeat)
        {
            outcome.Score -= DefeatPenalty;
        }
        return outcome;
    }

    /// <summary>
    /// Bosses give no post-combat rewards, so a large HpLost would unfairly
    /// sink every boss score. Reduce the HpLost weight by act:
    ///   3rd-act boss -> 0, 1st/2nd-act boss -> x0.5, others unchanged.
    /// </summary>
    public static double HpLostBossFactor(int bossLevel) => bossLevel switch
    {
        3 => 0.0,
        1 or 2 => 0.5,
        _ => 1.0,
    };

    public static double WeightedScore(Outcome o, ObjectiveWeights w, int bossLevel)
    {
        double hpFactor = HpLostBossFactor(bossLevel);
        return -w.HpLost * hpFactor * o.HpLost
             + w.Gold * o.GoldDelta
             + w.Potion * o.PotionDelta
             + w.MaxHp * o.MaxHpDelta
             + w.Relic * o.RelicCounterDelta
             + w.Card * o.CardDelta;
    }

    public static ObjectiveWeights CurrentWeights { get; } = new();

    /// <summary>Recompute cached scores on terminal nodes of a graph.</summary>
    public static void RefreshScores(CombatGraph graph)
    {
        if (graph.RootNodeId == null)
        {
            return;
        }
        var root = graph.GetNode(graph.RootNodeId);
        if (root == null)
        {
            return;
        }
        foreach (var n in graph.Nodes)
        {
            if (n.IsTerminal)
            {
                n.Outcome = ComputeOutcome(root, n, graph.BossLevel);
            }
        }
    }
}