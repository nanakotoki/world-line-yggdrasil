using MegaCrit.Sts2.Core.Combat;

namespace WorldLineYggdrasil.Simulation;

/// <summary>Configures the simulator's data resolvers from the live combat:
/// cards (runtime DynamicVars) and enemies (runtime move state machines).
/// Both read on demand from the current combat so no data is stored in the mod.</summary>
public static class SimSetup
{
    public static void Configure()
    {
        SimEngine.CardResolver = RuntimeCardData.BuildCombatCardResolver();

        RuntimeEnemyData.ClearCache();
        SimEngine.EnemyDefResolver = id => RuntimeEnemyData.GetEnemyDef(id) ?? EnemyDatabase.GetEnemy(id);
        SimEngine.EnemyMoveResolver = (enemyId, moveId) =>
            RuntimeEnemyData.GetMove(enemyId, moveId) ?? EnemyDatabase.GetMove(enemyId, moveId);
    }
}