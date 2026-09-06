using MegaCrit.Sts2.Core.Random;
using WorldLineYggdrasil.Graph;

namespace WorldLineYggdrasil.Simulation;

/// <summary>
/// A deterministic combat simulation state. Mirrors the fields that matter for
/// combat resolution; convertible to/from <see cref="CombatSnapshotData"/>.
/// </summary>
public sealed class SimState
{
    public int RoundNumber = 1;
    public int TurnNumber = 1;

    public int Hp;
    public int MaxHp;
    public int Block;
    public int Energy;
    public int MaxEnergy = 3;
    public int Gold;

    public List<string> Hand = new();
    public List<string> Draw = new();
    public List<string> Discard = new();
    public List<string> Exhaust = new();

    /// <summary>player powers: id -> amount</summary>
    public Dictionary<string, int> Powers = new();

    public List<string> Potions = new();

    public List<SimEnemy> Enemies = new();

    /// <summary>RNG for draws/reshuffles when the recorded draw order is exhausted.</summary>
    public System.Random Rng = new(12345);

    /// <summary>
    /// The real game's "Shuffle" RNG (Xoshiro256**), captured at sim-seed time so
    /// reshuffles reproduce the real game's card order exactly.
    /// </summary>
    public MegaRandom? ShuffleRng;
    /// <summary>MonsterAi RNG captured from the real run; drives random-branch move rolls.</summary>
    public MegaRandom? MonsterRng;

    /// <summary>Out-of-combat resource usage within this simulated line.</summary>
    public int PotionsUsed;
    public int PotionsDiscarded;
    public int GoldGained;

    public bool PlayerAlive => Hp > 0;
    public bool EnemiesAlive => Enemies.Any(e => e.Hp > 0);

    public SimState Clone()
    {
        return new SimState
        {
            RoundNumber = RoundNumber,
            TurnNumber = TurnNumber,
            Hp = Hp,
            MaxHp = MaxHp,
            Block = Block,
            Energy = Energy,
            MaxEnergy = MaxEnergy,
            Gold = Gold,
            Hand = new List<string>(Hand),
            Draw = new List<string>(Draw),
            Discard = new List<string>(Discard),
            Exhaust = new List<string>(Exhaust),
            Powers = new Dictionary<string, int>(Powers),
            Potions = new List<string>(Potions),
            Enemies = Enemies.Select(e => e.Clone()).ToList(),
            Rng = new System.Random(12345),
            ShuffleRng = CloneShuffleRng(),
            MonsterRng = CloneRng(MonsterRng),
            PotionsUsed = PotionsUsed,
            PotionsDiscarded = PotionsDiscarded,
            GoldGained = GoldGained,
        };
    }

    private static MegaRandom? CloneRng(MegaRandom? rng)
    {
        if (rng == null)
        {
            return null;
        }
        var ser = new MegaCrit.Sts2.Core.Saves.SerializableRng();
        rng.FillSerializableState(ser);
        return new MegaRandom(ser);
    }

    private MegaRandom? CloneShuffleRng()
    {
        if (ShuffleRng == null)
        {
            return null;
        }
        var ser = new MegaCrit.Sts2.Core.Saves.SerializableRng();
        ShuffleRng.FillSerializableState(ser);
        return new MegaRandom(ser);
    }

    public SimState CopyFrom(CombatSnapshotData d)
    {
        Hp = d.PlayerHp;
        MaxHp = d.PlayerMaxHp;
        Block = d.PlayerBlock;
        Energy = d.Energy;
        MaxEnergy = d.MaxEnergy;
        Gold = d.Gold;
        Hand = new List<string>(d.Hand);
        Draw = new List<string>(d.Draw);
        Discard = new List<string>(d.Discard);
        Exhaust = new List<string>(d.Exhaust);
        Powers = new Dictionary<string, int>(d.PlayerPowers);
        Potions = new List<string>(d.Potions);
        PotionsUsed = d.PotionsUsed;
        PotionsDiscarded = d.PotionsDiscarded;
        RoundNumber = d.RoundNumber;
        TurnNumber = d.TurnNumber;
        Enemies = d.Enemies.Select(e => new SimEnemy
        {
            Id = e.Id,
            Hp = e.Hp,
            Block = e.Block,
            Powers = new Dictionary<string, int>(e.Powers),
            Intent = e.Intent,
        }).ToList();
        // align move pattern index to the captured intent so the cycle advances correctly
        foreach (var e in Enemies)
        {
            var def = SimEngine.EnemyDefResolver(e.Id);
            if (def != null)
            {
                int idx = def.Moves.IndexOf(e.Intent);
                e.MoveIndex = idx >= 0 ? idx : 0;
            }
        }
        return this;
    }

    public CombatSnapshotData ToSnapshot()
    {
        var d = new CombatSnapshotData
        {
            RoundNumber = RoundNumber,
            TurnNumber = TurnNumber,
            Side = "Player",
            PlayerHp = Hp,
            PlayerMaxHp = MaxHp,
            PlayerBlock = Block,
            Energy = Energy,
            MaxEnergy = MaxEnergy,
            Gold = Gold,
            Hand = Hand.ToArray(),
            Draw = Draw.ToArray(),
            Discard = Discard.ToArray(),
            Exhaust = Exhaust.ToArray(),
            Play = [],
            PlayerPowers = new Dictionary<string, int>(Powers),
            Potions = Potions.ToArray(),
            PotionsUsed = PotionsUsed,
            PotionsDiscarded = PotionsDiscarded,
            GoldGained = GoldGained,
            Enemies = Enemies.Select(e => new EnemyData
            {
                Id = e.Id,
                Hp = e.Hp,
                Block = e.Block,
                Intent = e.Intent,
                Powers = new Dictionary<string, int>(e.Powers),
            }).ToList(),
        };
        return d;
    }
}

public sealed class SimEnemy
{
    public string Id = "";
    public int Hp;
    public int Block;
    public Dictionary<string, int> Powers = new();
    public string Intent = "";
    public int MoveIndex;
    /// <summary>Recent move ids (StateLog) for random-branch repeat rules.</summary>
    public List<string> RecentMoves = new();

    public SimEnemy Clone()
    {
        return new SimEnemy
        {
            Id = Id,
            Hp = Hp,
            Block = Block,
            Powers = new Dictionary<string, int>(Powers),
            Intent = Intent,
            MoveIndex = MoveIndex,
            RecentMoves = new List<string>(RecentMoves),
        };
    }
}