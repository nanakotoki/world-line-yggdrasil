using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using WorldLineYggdrasil.Graph;

namespace WorldLineYggdrasil.Capture;

/// <summary>
/// Registered as both a run-scoped and combat-scoped hook model. Builds the
/// combat graph in real time as the player plays:
///   nodes  = game states at player decision points,
///   edges  = the action taken (card / potion / end turn).
///
/// Recording rules:
///   - root node      : first player-turn start.
///   - mid-turn node  : after each manual card play / potion use / potion discard.
///   - end-turn node  : at the next player-turn start, labelled with "end_turn".
///   - terminal mark  : on victory or player death.
/// Identical situations merge into one node (semantic fingerprint), producing
/// cycles and self-loops.
/// </summary>
public sealed class CombatRecorderModel : AbstractModel
{
    public const string LogTag = "[WorldLineYggdrasil]";

    private CombatGraph _graph = new();

    public CombatGraph Graph => _graph;

    /// <summary>Finished combat graphs, preserved so run big nodes can link to them.</summary>
    public List<CombatGraph> ArchivedGraphs { get; } = new();

    /// <summary>Drops all archived combat graphs (called when a brand-new run starts).</summary>
    public void ClearArchivedGraphs() => ArchivedGraphs.Clear();

    public override bool ShouldReceiveCombatHooks => true;

    private string? _pendingAction;
    private uint? _pendingTargetId;
    private bool _started;
    private bool _captureScheduled;

    // Cumulative out-of-combat gain counters for the current combat.
    private int _goldGained;
    private int _potionsUsed;
    private int _potionsDiscarded;
    private int _potionsGenerated;

    public override Task BeforeCombatStart()
    {
        ArchiveGraph();
        _goldGained = 0;
        _potionsUsed = 0;
        _potionsDiscarded = 0;
        _potionsGenerated = 0;
        _graph.BossLevel = DetectBossLevel();
        return Task.CompletedTask;
    }

    private int DetectBossLevel()
    {
        try
        {
            var run = RunManager.Instance.DebugOnlyGetState();
            if (run == null)
            {
                return 0;
            }
            var room = run.CurrentRoom;
            if (room == null || room.RoomType != MegaCrit.Sts2.Core.Rooms.RoomType.Boss)
            {
                return 0;
            }
            return Math.Max(1, run.CurrentActIndex + 1);
        }
        catch
        {
            return 0;
        }
    }

    public override Task AfterCombatEnd(MegaCrit.Sts2.Core.Rooms.CombatRoom room)
    {
        // Combat ended. Ensure a terminal mark exists (after victory/death hooks,
        // or a fallback for special endings) so every finished combat gets a score.
        if (Graph.CurrentNodeId is { } id)
        {
            var node = Graph.GetNode(id);
            if (node != null && !node.IsTerminal)
            {
                string kind = DetermineEndKind();
                Graph.MarkTerminal(id, kind);
                Objective.ObjectiveService.RefreshScores(Graph);
                Log.Info($"{LogTag} terminal fallback {kind} at node {id}");
                Persistence.GraphExporter.Export(Graph, kind);
            }
        }
        return Task.CompletedTask;
    }

    private string DetermineEndKind()
    {
        var cs = CombatManager.Instance.DebugOnlyGetState();
        if (cs != null)
        {
            bool playerAlive = cs.PlayerCreatures.Any(c => c.IsAlive);
            bool enemiesAlive = cs.Enemies.Any(e => e.IsAlive);
            if (!playerAlive)
            {
                return "Defeat";
            }
            if (!enemiesAlive)
            {
                return "Victory";
            }
        }
        return "Ended";
    }

    public override Task AfterModifyingGoldGained(Player player, decimal amount)
    {
        // 注意：这个钩子只对 override 了 ModifyGoldGained 的模型触发（游戏过滤），
        // 本模型不会收到。金币实际由 AfterRewardTaken 用玩家金币增量计入。
        if (amount > 0)
        {
            _goldGained += (int)amount;
        }
        return Task.CompletedTask;
    }

    public override Task AfterPotionProcured(PotionModel potion)
    {
        _potionsGenerated++;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Auto-search restores arbitrary nodes and branches; reset the cumulative
    /// counters to the restored node's recorded values so each branch's outcome
    /// reflects only its own gains (not other branches' potion/gold usage).
    /// </summary>
    public void SyncCountersTo(GraphNode node)
    {
        var s = node.Snapshot;
        _goldGained = s.GoldGained;
        _potionsUsed = s.PotionsUsed;
        _potionsDiscarded = s.PotionsDiscarded;
        _potionsGenerated = s.PotionsGenerated;
    }

    public override Task AfterRewardTaken(Player player, MegaCrit.Sts2.Core.Rewards.Reward reward)
    {
        // Reward-screen gains (combat gold / potions / cards) happen AFTER the
        // combat graph's terminal is captured; fold them into the terminal
        // outcome so the score reflects the real out-of-combat gains.
        var term = Graph.GetNode(Graph.CurrentNodeId ?? "");
        if (term == null || !term.IsTerminal)
        {
            return Task.CompletedTask;
        }
        bool changed = false;
        switch (reward)
        {
            case MegaCrit.Sts2.Core.Rewards.GoldReward:
                // 用玩家金币增量（不依赖 gr.Amount 是否已 Populate）。
                // 终局快照的 Gold 是战斗结束时的金币，玩家当前 Gold 已含刚领的金币。
                int granted = player.Gold - term.Snapshot.Gold;
                if (granted > 0)
                {
                    term.Snapshot.GoldGained += granted;
                    term.Snapshot.Gold += granted;
                    changed = true;
                    Log.Info($"{LogTag} gold reward: granted {granted} -> GoldGained={term.Snapshot.GoldGained}");
                }
                break;
            case MegaCrit.Sts2.Core.Rewards.PotionReward:
                term.Snapshot.PotionsGenerated++;
                changed = true;
                break;
            case MegaCrit.Sts2.Core.Rewards.CardReward:
                term.Snapshot.CardsGained++;
                changed = true;
                break;
        }
        if (changed)
        {
            Objective.ObjectiveService.RefreshScores(Graph);
            Log.Info($"{LogTag} reward counted; terminal {term.Id.Substring(0, 8)} outcome updated");
        }
        return Task.CompletedTask;
    }

    private void ArchiveGraph()
    {
        if (_graph.Nodes.Count > 0)
        {
            Persistence.GraphExporter.Export(_graph, "combat_end");
            // 小图已取消：过去的战斗不保留小结点（战斗图仅本场有效），
            // 直接丢弃，不再归档供跨战斗跳转。
        }
        Restore.RestoreService.Reset();
        _graph = new CombatGraph();
        _started = false;
        _pendingAction = null;
    }

    /// <summary>First player-turn start creates the root node.</summary>
    public override Task AfterPlayerTurnStart(PlayerChoiceContext choiceContext, Player player)
    {
        // If a big-node restore queued a specific combat step, apply it once the
        // re-entered combat's cards exist. The apply is deferred and retried each
        // frame until it succeeds (the fresh combat may not have created its card
        // instances yet on the very first turn-start), with a hard cap.
        if (Restore.RestoreService.PendingSemanticRestore is { } pending)
        {
            ScheduleSemanticApply(pending, attempt: 0);
        }

        if (!_started)
        {
            _pendingAction = null;
            _pendingTargetId = null;
            CaptureNode();
            Log.Info($"{LogTag} root node recorded: {Graph.RootNodeId} scope={Graph.Scope}");
        }
        else
        {
            // Next decision point reached (covers the "end_turn" action edge).
            CaptureNode();
        }
        return Task.CompletedTask;
    }

    public override Task AfterCardPlayedLate(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        if (cardPlay.IsAutoPlay || !cardPlay.IsLastInSeries)
        {
            return Task.CompletedTask;
        }
        _pendingAction = cardPlay.Card.Id.Entry;
        _pendingTargetId = cardPlay.Target?.CombatId;
        ScheduleCapture();
        return Task.CompletedTask;
    }

    public override Task AfterPotionUsed(PotionModel potion, Creature? target)
    {
        _potionsUsed++;
        _pendingAction = "potion:" + potion.Id.Entry;
        _pendingTargetId = target?.CombatId;
        ScheduleCapture();
        return Task.CompletedTask;
    }

    public override Task AfterPotionDiscarded(PotionModel potion)
    {
        _potionsDiscarded++;
        _pendingAction = "discard_potion:" + potion.Id.Entry;
        _pendingTargetId = null;
        ScheduleCapture();
        return Task.CompletedTask;
    }

    /// <summary>The player's side ended -> next capture is the "end_turn" edge.</summary>
    public override Task AfterSideTurnEnd(PlayerChoiceContext choiceContext, CombatSide side, IEnumerable<Creature> participants)
    {
        if (side == CombatSide.Player)
        {
            _pendingAction = "end_turn";
            _pendingTargetId = null;
        }
        return Task.CompletedTask;
    }

    public override Task AfterCombatVictory(MegaCrit.Sts2.Core.Rooms.CombatRoom room)
    {
        MarkTerminal("Victory");
        return Task.CompletedTask;
    }

    public override Task AfterDeath(PlayerChoiceContext choiceContext, Creature creature, bool wasRemovalPrevented, float deathAnimLength)
    {
        if (creature.IsPlayer)
        {
            MarkTerminal("Defeat");
        }
        return Task.CompletedTask;
    }

    private void ScheduleCapture()
    {
        if (_captureScheduled)
        {
            return;
        }
        _captureScheduled = true;
        Godot.Callable.From(CaptureNode).CallDeferred();
    }

    /// <summary>Applies a queued big-node→combat-step semantic restore, retrying on
    /// later frames until the re-entered combat has cards (or giving up).</summary>
    private void ScheduleSemanticApply(CombatSnapshotData data, int attempt)
    {
        Godot.Callable.From(() =>
        {
            // a newer click replaced the pending request -> this one is obsolete
            if (!ReferenceEquals(Restore.RestoreService.PendingSemanticRestore, data))
            {
                return;
            }
            if (Restore.CombatSemanticRestore.ApplyToLive(data))
            {
                Restore.RestoreService.ClearPendingSemanticRestore();
                Log.Info($"{LogTag} applied pending semantic restore (attempt {attempt})");
            }
            else if (attempt < 90)
            {
                ScheduleSemanticApply(data, attempt + 1);
            }
            else
            {
                Restore.RestoreService.ClearPendingSemanticRestore();
                Restore.RestoreLogger.Warn($"{LogTag} semantic restore gave up after {attempt} attempts");
            }
        }).CallDeferred();
    }

    private void CaptureNode()
    {
        _captureScheduled = false;
        var snap = CombatStateReader.Capture();
        if (snap == null)
        {
            return;
        }

        // stamp cumulative out-of-combat gain counters into this snapshot
        snap.GoldGained = _goldGained;
        snap.PotionsUsed = _potionsUsed;
        snap.PotionsDiscarded = _potionsDiscarded;
        snap.PotionsGenerated = _potionsGenerated;

        if (Graph.Scope == "")
        {
            Graph.Reset(snap.CombatScope);
        }

        bool isNewNode = Graph.GetNode(Canonicalizer.Fingerprint(snap)) == null;
        var node = Graph.AddOrUpdateNode(snap, isTerminal: false, terminalKind: null);

        if (_started)
        {
            string action = _pendingAction ?? "?";
            Graph.AddEdge(Graph.CurrentNodeId!, node.Id, action, _pendingTargetId);
        }
        else
        {
            _started = true;
        }

        Graph.SetCurrent(node.Id);
        _pendingAction = null;
        _pendingTargetId = null;

        Restore.RestoreService.StoreDeferred(node.Id);

        Log.Info($"{LogTag} node {node.Id}{(isNewNode ? " new" : " merged")} total={Graph.Nodes.Count} edges={Graph.Edges.Count}");
    }

    private void MarkTerminal(string kind)
    {
        if (Graph.CurrentNodeId != null)
        {
            Graph.MarkTerminal(Graph.CurrentNodeId, kind);
            Objective.ObjectiveService.RefreshScores(Graph);
            var term = Graph.GetNode(Graph.CurrentNodeId);
            Log.Info($"{LogTag} terminal {kind} at node {Graph.CurrentNodeId}" +
                (term?.Outcome != null ? $" outcome[{term.Outcome.Summary()}] score={term.Outcome.Score:F2}" : ""));
            Persistence.GraphExporter.Export(Graph, kind);
        }
    }
}