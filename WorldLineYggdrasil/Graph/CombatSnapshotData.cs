using MegaCrit.Sts2.Core.Saves;

namespace WorldLineYggdrasil.Graph;

/// <summary>
/// A serializable, RNG-free summary of a combat state, used for:
///  - node identity (canonical fingerprint),
///  - node display labels,
///  - objective scoring (M3),
///  - future offline-simulator interop (M4).
///
/// Deliberately excludes the RNG cursor so identical *situations* merge into one
/// node (semantic equivalence), which is what makes cycles/self-loops appear.
/// The full RNG state needed for restore is captured separately (M1 restore).
/// </summary>
public sealed class CombatSnapshotData
{
    public int RoundNumber { get; set; } = 1;
    public int TurnNumber { get; set; } = 1;
    public string Side { get; set; } = "";

    public int PlayerHp { get; set; }
    public int PlayerMaxHp { get; set; }
    public int PlayerBlock { get; set; }
    public int Energy { get; set; }
    public int Stars { get; set; }
    public int Gold { get; set; }
    public int MaxEnergy { get; set; }

    /// <summary>Ordered card ids per pile.</summary>
    public string[] Hand { get; set; } = [];
    public string[] Draw { get; set; } = [];
    public string[] Discard { get; set; } = [];
    public string[] Exhaust { get; set; } = [];
    public string[] Play { get; set; } = [];

    /// <summary>power id -> amount</summary>
    public Dictionary<string, int> PlayerPowers { get; set; } = new();

    public string[] Relics { get; set; } = [];

    /// <summary>relic id -> StackCount (proxy for relic progress counters).</summary>
    public Dictionary<string, int> RelicCounters { get; set; } = new();

    public string[] Potions { get; set; } = [];

    /// <summary>Cumulative out-of-combat gain counters up to this state.</summary>
    public int GoldGained { get; set; }
    public int PotionsUsed { get; set; }
    public int PotionsDiscarded { get; set; }
    public int PotionsGenerated { get; set; }
    public int CardsGained { get; set; }

    public List<EnemyData> Enemies { get; set; } = new();

    /// <summary>
    /// The combat id / encounter info, if known. Used to scope a graph to a combat.
    /// </summary>
    public string CombatScope { get; set; } = "";

    // ---- full cross-combat restore fields (captured live; IGNORED by the
    // canonical fingerprint so identical situations still merge) ----

    /// <summary>run-level RNG queue name (RunRngType) -> serialized state.</summary>
    public Dictionary<string, SerializableRng> RunRngs { get; set; } = new();

    /// <summary>player-level RNG (PlayerRngType: Rewards/Shops/Transformations).</summary>
    public Dictionary<string, SerializableRng> PlayerRngs { get; set; } = new();

    /// <summary>player potion slot contents (null = empty slot), in slot order.</summary>
    public List<string?> PotionSlots { get; set; } = new();

    /// <summary>player orb ids in channeled order.</summary>
    public List<string> Orbs { get; set; } = new();
}

public sealed class EnemyData
{
    public string Id { get; set; } = "";
    public int Hp { get; set; }
    public int Block { get; set; }
    public string Intent { get; set; } = "";
    public Dictionary<string, int> Powers { get; set; } = new();

    /// <summary>per-monster RNG (monster._rng).</summary>
    public SerializableRng? MonsterRng { get; set; }

    /// <summary>move state machine current-state id (monster._moveStateMachine._currentState.Id).</summary>
    public string? CurrentStateId { get; set; }

    /// <summary>recent move history (StateLog ids), for pattern-gated next moves.</summary>
    public List<string> StateLogIds { get; set; } = new();

    /// <summary>monster._performedFirstMove / _spawnedThisTurn flags.</summary>
    public bool PerformedFirstMove { get; set; }
    public bool SpawnedThisTurn { get; set; }
}