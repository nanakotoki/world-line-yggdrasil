using MegaCrit.Sts2.Core.MonsterMoves;

namespace WorldLineYggdrasil.Simulation;

public enum SimActionKind
{
    PlayCard,
    EndTurn,
    UsePotion,
    DiscardPotion,
}

/// <summary>A single player action in the simulator.</summary>
public sealed class SimAction
{
    public SimActionKind Kind { get; init; }
    /// <summary>Index into the hand (for PlayCard / UsePotion / DiscardPotion).</summary>
    public int CardIndex { get; init; } = -1;
    /// <summary>Index into the enemy list; -1 = no target.</summary>
    public int TargetIndex { get; init; } = -1;

    public override string ToString() => Kind switch
    {
        SimActionKind.PlayCard => $"play[{CardIndex}](t={TargetIndex})",
        SimActionKind.EndTurn => "end_turn",
        SimActionKind.UsePotion => $"potion[{CardIndex}]",
        SimActionKind.DiscardPotion => $"discard[{CardIndex}]",
        _ => "?",
    };
}

public enum SimEffectType
{
    Attack,
    Block,
    Draw,
    GainEnergy,
    ApplyPowerToTarget,
    ApplyPowerToSelf,
    AddStatusToDraw,
    LoseHp,
}

public sealed class SimEffect
{
    public SimEffectType Type { get; init; }
    public int Amount { get; init; }
    public string PowerId { get; init; } = "";
}

public sealed class SimCard
{
    public string Id { get; init; } = "";
    public int Cost { get; init; }
    public bool NeedsTarget { get; init; }
    public List<SimEffect> Effects { get; init; } = new();
    public bool IsStatus { get; init; }
    public bool IsEthereal { get; init; }
    public bool IsExhaust { get; init; }
    /// <summary>Gains block equal to the damage this card deals (Fisticuffs).</summary>
    public bool BlockEqualsDamageDealt { get; init; }
}

/// <summary>An enemy's move (one intent).</summary>
public sealed class SimMove
{
    public string Id { get; set; } = "";
    public int Damage { get; set; }
    public int Block { get; set; }
    public string ApplyPowerToSelf { get; set; } = "";
    public int ApplyPowerToSelfAmount { get; set; }
    public string ApplyPowerToPlayer { get; set; } = "";
    public int ApplyPowerToPlayerAmount { get; set; }
    public int HealSelf { get; set; }
    /// <summary>Status card id added to the player's discard pile.</summary>
    public string StatusToPlayerDraw { get; set; } = "";
    /// <summary>How many copies of the status card are added.</summary>
    public int StatusCount { get; set; } = 1;
}

public sealed class SimEnemyDef
{
    public string Id { get; init; } = "";
    /// <summary>Cyclic move pattern (move ids).</summary>
    public List<string> Moves { get; init; } = new();
    /// <summary>Per-move followup: Moves[NextIndex[i]] is the next move after Moves[i].
    /// Mirrors the state machine graph (chains + loops). Null means ring behavior.</summary>
    public int[]? NextIndex { get; set; }
    /// <summary>Random-branch followups per move id (RandomBranchState): each entry is a
    /// possible next move with its repeat rules. When present, the simulator rolls the
    /// next move with the captured MonsterAi RNG instead of following NextIndex.</summary>
    public Dictionary<string, List<SimBranch>>? Branches { get; set; }
}

public sealed class SimBranch
{
    public string NextId { get; init; } = "";
    public MoveRepeatType RepeatType { get; init; }
    public int MaxRepeats { get; init; }
}