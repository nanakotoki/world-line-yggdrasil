using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Runs;
using WorldLineYggdrasil.Capture;
using WorldLineYggdrasil.Graph;
using WorldLineYggdrasil.Simulation;

namespace WorldLineYggdrasil.Validation;

public sealed class ValidationResult
{
    public bool Valid { get; set; }
    public int DivergedAtStep { get; set; } = -1;
    public string? Message { get; set; }
    public List<string> StepLog { get; set; } = new();
}

/// <summary>
/// Replays a simulator plan in the REAL game, action by action, comparing the
/// full combat state (HP / block / energy and every pile) after each step.
/// Valid = the simulated "optimal" line actually holds in the real game.
/// </summary>
public static class PlanValidator
{
    public static async Task<ValidationResult> Validate(SimPlan plan)
    {
        var result = new ValidationResult();
        var graph = ModEntry.Recorder.Graph;
        string start = graph.CurrentNodeId ?? graph.RootNodeId ?? "";
        if (start == "")
        {
            result.Message = "no combat graph";
            return result;
        }
        if (plan.Actions.Count == 0)
        {
            result.Message = "empty plan";
            return result;
        }

        // restore the live game to the start node, then seed the sim from it
        if (!Restore.RestoreService.RestoreTo(start))
        {
            result.Message = "cannot restore start node";
            return result;
        }
        graph.SetCurrent(start);
        await Task.Delay(100);

        SimSetup.Configure();
        SimEngine.EnemyDiagnostics = true;
        var sim = new SimState().CopyFrom(graph.GetNode(start)!.Snapshot);
        sim.ShuffleRng = RuntimeCardData.CaptureShuffleRng();
        sim.MonsterRng = RuntimeCardData.CaptureMonsterRng();

        // sanity: the restored real combat should match the start snapshot's
        // enemy HP; a mismatch means the restore re-initialized enemies to full
        var liveStart = CombatStateReader.Capture();
        if (liveStart != null)
        {
            var startSnap = graph.GetNode(start)!.Snapshot;
            Log.Info($"[WorldLineYggdrasil.Validate] start real-eh=[{string.Join(",", liveStart.Enemies.Select(x => x.Hp))}] snap-eh=[{string.Join(",", startSnap.Enemies.Select(x => x.Hp))}]");
        }

        for (int i = 0; i < plan.Actions.Count; i++)
        {
            var pa = plan.Actions[i];

            // 1) advance the simulator
            var simAction = ToSimAction(sim, pa);
            if (simAction == null)
            {
                result.Message = $"step {i}: cannot simulate '{pa}' (card not in sim hand)";
                return result;
            }
            SimEngine.Apply(sim, simAction);

            // 2) inject the same action in the real game and wait for settlement
            string parentId = graph.CurrentNodeId ?? start;
            if (!await TryInjectReal(pa))
            {
                result.Message = $"step {i}: real-game inject failed for '{pa}'";
                return result;
            }
            string settledId = await WaitForSettle(parentId, graph);

            // 3) compare real vs sim
            var realNode = graph.GetNode(settledId);
            if (realNode == null)
            {
                // combat ended (winning final action) and no node captured.
                // If the simulator also reached a terminal state, the plan is
                // valid even if it predicted more actions than the real needed.
                if (SimEngine.IsTerminal(sim))
                {
                    result.Valid = true;
                    result.Message = $"combat ended at step {i} ({SimEngine.TerminalKind(sim)})";
                    result.StepLog.Add($"step {i}: {pa} OK (combat ended, {SimEngine.TerminalKind(sim)})");
                    Log.Info($"[WorldLineYggdrasil.Validate] RESULT: VALID - combat ended at step {i} ({SimEngine.TerminalKind(sim)}), plan had {plan.Actions.Count} steps");
                    return result;
                }
                result.Message = $"step {i}: real state unavailable";
                return result;
            }
            var realSnap = realNode.Snapshot;
            var simSnap = sim.ToSnapshot();
            var diffs = Compare(realSnap, simSnap);
            if (diffs.Count > 0)
            {
                result.DivergedAtStep = i;
                result.Message = $"diverged at step {i}: {string.Join("; ", diffs)}";
                result.StepLog.Add($"step {i}: {pa} DIVERGED ({string.Join("; ", diffs)})");
                Log.Warn($"[WorldLineYggdrasil.Validate] step {i}: {pa} DIVERGED ({string.Join("; ", diffs)})");
                return result;
            }
result.StepLog.Add($"step {i}: {pa} OK (hp={realSnap.PlayerHp} e={realSnap.Energy})");
                Log.Info($"[WorldLineYggdrasil.Validate] step {i}: {pa} OK (hp={realSnap.PlayerHp} e={realSnap.Energy} eh=[{string.Join(",", realSnap.Enemies.Select(x => x.Hp))}] sim-eh=[{string.Join(",", simSnap.Enemies.Select(x => x.Hp))}])");
        }

        result.Valid = true;
        result.Message = "all steps matched the real game";
        Log.Info($"[WorldLineYggdrasil.Validate] RESULT: VALID — all {plan.Actions.Count} steps matched");
        return result;
    }

    private static async Task<string> WaitForSettle(string parentId, CombatGraph graph)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(60);
            var cur = graph.CurrentNodeId;
            if (cur != null && cur != parentId)
            {
                return cur;
            }
            if (!CombatManager.Instance.IsInProgress)
            {
                return parentId; // combat ended
            }
        }
        return parentId;
    }

    private static async Task<bool> TryInjectReal(SimPlanAction pa)
    {
        var cs = CombatManager.Instance.DebugOnlyGetState();
        var player = cs != null ? LocalContext.GetMe(cs) : null;
        if (player?.PlayerCombatState == null)
        {
            return false;
        }
        try
        {
            switch (pa.Kind)
            {
                case SimPlanActionKind.PlayCard:
                    var card = player.PlayerCombatState.Hand.Cards.FirstOrDefault(c => c.Id.Entry == pa.CardId);
                    if (card == null)
                    {
                        return false;
                    }
                    var target = TargetByIndex(cs, pa.TargetIndex);
                    RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new PlayCardAction(card, target));
                    return true;
                case SimPlanActionKind.EndTurn:
                    PlayerCmd.EndTurn(player, canBackOut: false);
                    return true;
                case SimPlanActionKind.UsePotion:
                    var potion = player.Potions.FirstOrDefault(p => p.Id.Entry == pa.CardId);
                    if (potion == null)
                    {
                        return false;
                    }
                    potion.EnqueueManualUse(player.Creature);
                    return true;
                case SimPlanActionKind.DiscardPotion:
                    var pot = player.Potions.FirstOrDefault(p => p.Id.Entry == pa.CardId);
                    if (pot == null)
                    {
                        return false;
                    }
                    int slot = pot.Owner.GetPotionSlotIndex(pot);
                    RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
                        new DiscardPotionGameAction(pot.Owner, (uint)slot, isCombatInProgress: true));
                    return true;
            }
        }
        catch
        {
        }
        return false;
    }

    private static Creature? TargetByIndex(CombatState cs, int index)
    {
        var enemies = cs.Enemies.Where(e => e.IsAlive).ToList();
        return index >= 0 && index < enemies.Count ? enemies[index] : null;
    }

    private static SimAction? ToSimAction(SimState s, SimPlanAction pa)
    {
        return pa.Kind switch
        {
            SimPlanActionKind.EndTurn => new SimAction { Kind = SimActionKind.EndTurn },
            SimPlanActionKind.PlayCard =>
                s.Hand.IndexOf(pa.CardId) is int i && i >= 0
                    ? new SimAction { Kind = SimActionKind.PlayCard, CardIndex = i, TargetIndex = pa.TargetIndex }
                    : null,
            SimPlanActionKind.DiscardPotion =>
                s.Potions.IndexOf(pa.CardId) is int j && j >= 0
                    ? new SimAction { Kind = SimActionKind.DiscardPotion, CardIndex = j }
                    : null,
            _ => null,
        };
    }

    private static List<string> Compare(CombatSnapshotData real, CombatSnapshotData sim)
    {
        var diffs = new List<string>();
        if (real.PlayerHp != sim.PlayerHp)
        {
            diffs.Add($"hp {real.PlayerHp} vs sim {sim.PlayerHp}");
        }
        if (real.PlayerBlock != sim.PlayerBlock)
        {
            diffs.Add($"block {real.PlayerBlock} vs sim {sim.PlayerBlock}");
        }
        if (real.Energy != sim.Energy)
        {
            diffs.Add($"energy {real.Energy} vs sim {sim.Energy}");
        }
        ComparePile(diffs, "hand", real.Hand, sim.Hand);
        ComparePile(diffs, "draw", real.Draw, sim.Draw);
        // the real game parks the just-played card in the Play pile momentarily
        // before it reaches the discard pile; union them for a fair comparison.
        ComparePile(diffs, "discard", real.Discard.Concat(real.Play).ToArray(), sim.Discard);
        ComparePile(diffs, "exhaust", real.Exhaust, sim.Exhaust);

        // enemy intents (which move each enemy will use next)
        for (int i = 0; i < Math.Min(real.Enemies.Count, sim.Enemies.Count); i++)
        {
            if (real.Enemies[i].Intent != sim.Enemies[i].Intent)
            {
                diffs.Add($"enemy{i}({real.Enemies[i].Id}) intent {real.Enemies[i].Intent} vs sim {sim.Enemies[i].Intent}");
            }
            if (real.Enemies[i].Hp != sim.Enemies[i].Hp)
            {
                diffs.Add($"enemy{i}({real.Enemies[i].Id}) hp {real.Enemies[i].Hp} vs sim {sim.Enemies[i].Hp}");
            }
        }
        return diffs;
    }

    private static void ComparePile(List<string> diffs, string name, string[] a, string[] b)
    {
        var sa = a.OrderBy(x => x, System.StringComparer.Ordinal).ToList();
        var sb = b.OrderBy(x => x, System.StringComparer.Ordinal).ToList();
        if (!sa.SequenceEqual(sb))
        {
            diffs.Add($"{name}[{string.Join(",", sa)}] vs sim[{string.Join(",", sb)}]");
        }
    }
}