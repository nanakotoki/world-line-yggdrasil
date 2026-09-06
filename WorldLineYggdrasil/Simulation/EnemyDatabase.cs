namespace WorldLineYggdrasil.Simulation;

/// <summary>
/// Enemy definitions (moves + cyclic patterns) used by the simulator.
/// Move values verified against the decompiled monster classes.
/// </summary>
public static class EnemyDatabase
{
    private static readonly Dictionary<string, SimEnemyDef> Enemies = new();
    private static readonly Dictionary<string, SimMove> Moves = new();

    static EnemyDatabase()
    {
        Move("TACKLE_MOVE", new SimMove { Id = "TACKLE_MOVE", Damage = 3 });
        Move("GOOP_MOVE", new SimMove { Id = "GOOP_MOVE", StatusToPlayerDraw = "SLIMED" });
        Move("CLUMP_SHOT", new SimMove { Id = "CLUMP_SHOT", Damage = 8 });
        Move("STICKY_SHOT", new SimMove { Id = "STICKY_SHOT", StatusToPlayerDraw = "SLIMED" });
        Move("STICKY_SHOT_MOVE", new SimMove { Id = "STICKY_SHOT_MOVE", StatusToPlayerDraw = "SLIMED" });
        Move("POKEY_POUNCE_MOVE", new SimMove { Id = "POKEY_POUNCE_MOVE", Damage = 11 });

        Move("SHRINKER_MOVE", new SimMove { Id = "SHRINKER_MOVE", Damage = 2, ApplyPowerToPlayer = "WEAK", ApplyPowerToPlayerAmount = 1 });
        Move("CHOMP_MOVE", new SimMove { Id = "CHOMP_MOVE", Damage = 8 });
        Move("STOMP_MOVE", new SimMove { Id = "STOMP_MOVE", Damage = 14 });

        Enemy("LEAF_SLIME_S", new SimEnemyDef { Id = "LEAF_SLIME_S", Moves = { "GOOP_MOVE", "TACKLE_MOVE" } });
        Enemy("LEAF_SLIME_M", new SimEnemyDef { Id = "LEAF_SLIME_M", Moves = { "STICKY_SHOT", "CLUMP_SHOT" } });
        Enemy("TWIG_SLIME_S", new SimEnemyDef { Id = "TWIG_SLIME_S", Moves = { "TACKLE_MOVE" } });
        Enemy("TWIG_SLIME_M", new SimEnemyDef { Id = "TWIG_SLIME_M", Moves = { "STICKY_SHOT_MOVE", "POKEY_POUNCE_MOVE" } });
        Enemy("SHRINKER_BEETLE", new SimEnemyDef
        {
            Id = "SHRINKER_BEETLE",
            Moves = { "SHRINKER_MOVE", "CHOMP_MOVE", "STOMP_MOVE" },
            NextIndex = new[] { 1, 2, 1 },
        });
    }

    private static void Move(string id, SimMove m) => Moves[id] = m;
    private static void Enemy(string id, SimEnemyDef d) => Enemies[id] = d;

    public static SimEnemyDef? GetEnemy(string id) => Enemies.TryGetValue(id, out var d) ? d : null;

    public static SimMove? GetMove(string enemyId, string intent)
    {
        if (Moves.TryGetValue(intent, out var m))
        {
            return m;
        }
        return null;
    }
}