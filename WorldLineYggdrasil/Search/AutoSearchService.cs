using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Runs;
using WorldLineYggdrasil.Graph;

namespace WorldLineYggdrasil.Search;

/// <summary>
/// Drives the REAL game as the simulator: explores the combat state space by
/// restoring recorded deep snapshots, injecting legal actions (real game
/// objects), and letting the existing recorder capture the resulting nodes.
/// BFS finds the fewest-action victory; the explored graph merges with manual
/// exploration (same fingerprint/identity).
/// </summary>
public static class AutoSearchService
{
    public static bool IsSearching { get; private set; }
    public static int EdgeCount { get; private set; }
    public static int TerminalCount { get; private set; }
    public static bool StopRequested { get; set; }

    public enum SearchMode
    {
        Greedy,
        Bfs,
        Beam,
    }

    public static async Task RunSearch(int nodeBudget, bool fromRoot, SearchMode mode)
    {
        if (IsSearching)
        {
            return;
        }
        IsSearching = true;
        SearchSpeed.Begin();
        StopRequested = false;
        try
        {
            var graph = ModEntry.Recorder.Graph;
            if (graph.Nodes.Count == 0)
            {
                return;
            }
            string? start = fromRoot ? graph.RootNodeId : graph.CurrentNodeId;
            if (start == null)
            {
                return;
            }
            EdgeCount = 0;
            TerminalCount = 0;
            switch (mode)
            {
                case SearchMode.Greedy:
                    bool won = await GreedyExplore(start, nodeBudget, graph);
                    Log.Info($"[WorldLineYggdrasil.Search] greedy done: edges={EdgeCount} won={won}");
                    break;
                case SearchMode.Bfs:
                    await BfsExplore(start, nodeBudget, graph);
                    Log.Info($"[WorldLineYggdrasil.Search] bfs done: edges={EdgeCount} terminals={TerminalCount}");
                    break;
                case SearchMode.Beam:
                    await BeamExplore(start, nodeBudget, graph);
                    Log.Info($"[WorldLineYggdrasil.Search] beam done: edges={EdgeCount} terminals={TerminalCount}");
                    break;
            }
        }
        finally
        {
            SearchSpeed.End();
            IsSearching = false;
        }
    }

    private static async Task BfsExplore(string start, int nodeBudget, CombatGraph graph)
    {
        var frontier = new Queue<string>();
        frontier.Enqueue(start);
        var visited = new HashSet<string>();
        while (frontier.Count > 0 && EdgeCount < nodeBudget && CombatManager.Instance.IsInProgress && !StopRequested)
        {
            var nodeId = frontier.Dequeue();
            if (!visited.Add(nodeId))
            {
                continue;
            }
            var node = graph.GetNode(nodeId);
            if (node == null || node.IsTerminal)
            {
                continue;
            }
            if (!Restore.RestoreService.RestoreTo(nodeId))
            {
                continue;
            }
            graph.SetCurrent(nodeId);
            await Task.Delay(80);
            var actions = ActionEnumerator.Enumerate();
            foreach (var action in actions)
            {
                if (EdgeCount >= nodeBudget || StopRequested)
                {
                    break;
                }
                var childId = await TryAction(nodeId, action, graph);
                if (childId == null)
                {
                    continue;
                }
                frontier.Enqueue(childId);
                if (graph.GetNode(childId)?.IsTerminal == true)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Depth-first search guided by a simple greedy heuristic: kill enemies
    /// first, then deal damage, then block, then end turn. Finds a winning path
    /// fast for real combats where BFS blows up.
    /// </summary>
    private static async Task<bool> GreedyExplore(string nodeId, int nodeBudget, CombatGraph graph)
    {
        var node = graph.GetNode(nodeId);
        if (node == null)
        {
            return false;
        }
        if (node.IsTerminal)
        {
            return node.TerminalKind == "Victory";
        }
        if (EdgeCount >= nodeBudget || StopRequested)
        {
            return false;
        }

        if (!Restore.RestoreService.RestoreTo(nodeId))
        {
            return false;
        }
        graph.SetCurrent(nodeId);
        await Task.Delay(80);
        if (!CombatManager.Instance.IsInProgress)
        {
            return false;
        }

        var actions = ActionEnumerator.Enumerate();
        foreach (var action in RankActions(actions, node.Snapshot))
        {
            if (EdgeCount >= nodeBudget || StopRequested)
            {
                return false;
            }
            var childId = await TryAction(nodeId, action, graph);
            if (childId == null)
            {
                continue;
            }
            var child = graph.GetNode(childId);
            if (child == null)
            {
                continue;
            }
            if (child.IsTerminal)
            {
                if (child.TerminalKind == "Victory")
                {
                    Log.Info($"[WorldLineYggdrasil.Search] VICTORY via '{action.Label}' at node {childId.Substring(0, 6)}");
                    return true;
                }
                continue; // defeat: backtrack, try next action
            }
            if (await GreedyExplore(childId, nodeBudget, graph))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Beam search: keeps the K most promising frontier nodes (by a heuristic)
    /// and explores them in parallel. Finds a good win for real combats where
    /// BFS blows up; the found terminal's M3 score is used to mark the best path.
    /// </summary>
    private static async Task BeamExplore(string start, int nodeBudget, CombatGraph graph, int beamWidth = 5)
    {
        var visited = new HashSet<string>();
        var terminals = new List<GraphNode>();
        var frontier = new List<(string id, double h)> { (start, Heuristic(graph.GetNode(start)?.Snapshot)) };

        while (frontier.Count > 0 && EdgeCount < nodeBudget && CombatManager.Instance.IsInProgress && !StopRequested)
        {
            var next = new List<(string id, double h)>();
            foreach (var (nodeId, _) in frontier)
            {
                if (StopRequested || EdgeCount >= nodeBudget || !CombatManager.Instance.IsInProgress)
                {
                    break;
                }
                if (!visited.Add(nodeId))
                {
                    continue;
                }
                var node = graph.GetNode(nodeId);
                if (node == null || node.IsTerminal)
                {
                    continue;
                }
                if (!Restore.RestoreService.RestoreTo(nodeId))
                {
                    continue;
                }
                graph.SetCurrent(nodeId);
                await Task.Delay(80);

                var actions = ActionEnumerator.Enumerate();
                foreach (var action in actions)
                {
                    if (StopRequested || EdgeCount >= nodeBudget)
                    {
                        break;
                    }
                    var childId = await TryAction(nodeId, action, graph);
                    if (childId == null)
                    {
                        continue;
                    }
                    var child = graph.GetNode(childId);
                    if (child == null)
                    {
                        continue;
                    }
                    if (child.IsTerminal)
                    {
                        terminals.Add(child);
                        continue;
                    }
                    if (!visited.Contains(childId))
                    {
                        next.Add((childId, Heuristic(child.Snapshot)));
                    }
                }
            }
            if (terminals.Count > 0)
            {
                break; // combat ended on a winning line; stop exploring
            }
            frontier = next.OrderByDescending(n => n.h).Take(beamWidth).ToList();
        }

        if (terminals.Count > 0)
        {
            Objective.ObjectiveService.RefreshScores(graph);
            var best = terminals.OrderByDescending(t => t.Outcome?.Score ?? double.MinValue).First();
            graph.SetBestPath(best.Id);
            Log.Info($"[WorldLineYggdrasil.Search] beam best: {best.Id.Substring(0, 6)} [{best.TerminalKind}] score={best.Outcome?.Score:F2} {best.Outcome?.Summary()}");
        }
        else
        {
            Log.Info("[WorldLineYggdrasil.Search] beam: no terminal found within budget");
        }
    }

    /// <summary>Heuristic: prefer high player HP while pushing enemies toward death.</summary>
    private static double Heuristic(Graph.CombatSnapshotData? s)
    {
        if (s == null)
        {
            return 0;
        }
        double enemyHp = s.Enemies.Sum(e => Math.Max(0, e.Hp));
        return s.PlayerHp - enemyHp * 0.05;
    }

    private static async Task<string?> TryAction(string parentId, RealAction action, CombatGraph graph)
    {
        if (!Restore.RestoreService.RestoreTo(parentId))
        {
            return null;
        }
        graph.SetCurrent(parentId);
        var parent = graph.GetNode(parentId);
        if (parent != null)
        {
            ModEntry.Recorder.SyncCountersTo(parent);
        }
        await Task.Delay(80);
        if (!Inject(action))
        {
            return null;
        }
        string childId = await ResolveChild(parentId, graph);
        EdgeCount++;
        var child = graph.GetNode(childId);
        if (child != null)
        {
            string? kind = TerminalKind(child.Snapshot);
            if (kind != null && !child.IsTerminal)
            {
                graph.MarkTerminal(childId, kind);
                Objective.ObjectiveService.RefreshScores(graph);
                TerminalCount++;
            }
            Log.Info($"[WorldLineYggdrasil.Search] {parentId.Substring(0, 6)} --{action.Label}--> {childId.Substring(0, 6)} hp={child.Snapshot.PlayerHp}{(child.IsTerminal ? " [" + child.TerminalKind + "]" : "")} edges={EdgeCount}");
        }
        return childId;
    }

    private static List<RealAction> RankActions(List<RealAction> actions, Graph.CombatSnapshotData state)
    {
        // score: kills > damage > block > potions > end turn
        return actions
            .OrderByDescending(a => ScoreAction(a, state))
            .ToList();
    }

    private static double ScoreAction(RealAction a, Graph.CombatSnapshotData state)
    {
        switch (a.Kind)
        {
            case RealActionKind.PlayCard:
                var card = Simulation.CardDatabase.Get(a.Card?.Id.Entry ?? "");
                if (card == null)
                {
                    return -1;
                }
                double score = 0;
                foreach (var eff in card.Effects)
                {
                    if (eff.Type == Simulation.SimEffectType.Attack)
                    {
                        // prefer killing a low-HP enemy
                        int targetHp = a.Target?.CurrentHp ?? int.MaxValue;
                        score += eff.Amount >= targetHp ? 1000 : eff.Amount;
                    }
                    else if (eff.Type == Simulation.SimEffectType.Block)
                    {
                        score += 1;
                    }
                }
                return score;
            case RealActionKind.EndTurn:
                return -5;
            case RealActionKind.UsePotion:
                return 2;
            case RealActionKind.DiscardPotion:
                return -2;
            default:
                return -3;
        }
    }

    private static bool Inject(RealAction action)
    {
        try
        {
            switch (action.Kind)
            {
                case RealActionKind.PlayCard:
                    if (action.Card == null)
                    {
                        return false;
                    }
                    RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new PlayCardAction(action.Card, action.Target));
                    return true;

                case RealActionKind.EndTurn:
                    var cs = CombatManager.Instance.DebugOnlyGetState();
                    var player = cs != null ? LocalContext.GetMe(cs) : null;
                    if (player == null)
                    {
                        return false;
                    }
                    PlayerCmd.EndTurn(player, canBackOut: false);
                    return true;

                case RealActionKind.UsePotion:
                    if (action.Potion == null)
                    {
                        return false;
                    }
                    action.Potion.EnqueueManualUse(action.Target);
                    return true;

                case RealActionKind.DiscardPotion:
                    if (action.Potion?.Owner == null)
                    {
                        return false;
                    }
                    int slot = action.Potion.Owner.GetPotionSlotIndex(action.Potion);
                    if (slot < 0)
                    {
                        return false;
                    }
                    RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
                        new DiscardPotionGameAction(action.Potion.Owner, (uint)slot, isCombatInProgress: true));
                    return true;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[WorldLineYggdrasil.Search] inject failed for {action.Label}: {ex.Message}");
        }
        return false;
    }

    /// <summary>Waits for the recorder to capture the resulting node; falls back to
    /// the parent when the combat ends first (winning final action).</summary>
    private static async Task<string> ResolveChild(string parentId, CombatGraph graph)
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
                return parentId; // combat ended; parent is the terminal state
            }
        }
        return parentId;
    }

    private static string? TerminalKind(Graph.CombatSnapshotData s)
    {
        if (s.PlayerHp <= 0)
        {
            return "Defeat";
        }
        if (s.Enemies.All(e => e.Hp <= 0))
        {
            return "Victory";
        }
        return null;
    }
}