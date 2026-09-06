using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;
using WorldLineYggdrasil.Graph;

namespace WorldLineYggdrasil.Restore;

/// <summary>
/// Debug console commands for the World Line Yggdrasil graph + restore:
///   wly nodes              list current combat graph nodes (numbered)
///   wly restore &lt;n&gt;        restore to node by 1-based index (from wly nodes)
///   wly restore &lt;id&gt;       restore to node by full/prefix id
///   wly back               rewind one step (restore to the parent of current)
///   wly dump               export the current graph to JSON
/// </summary>
public class WlyConsoleCmd : AbstractConsoleCmd
{
    public override string CmdName => "wly";
    public override string Args => "<nodes|restore <n|id>|back|run|runrestore <n>|step <b> <s>|outcome|weight <f> <v>|simsearch [b]|validate|optimal [b] [fb]|search [b]|dump>";
    public override string Description => "World Line Yggdrasil: combat graph + run tree + outcome scoring.";
    public override bool IsNetworked => false;
    public override bool DebugOnly => true;

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        if (args.Length < 1)
        {
            return new CmdResult(false, "wly <nodes|restore <n|id>|back|dump>");
        }

        switch (args[0].ToLowerInvariant())
        {
            case "nodes":
                return ListNodes();
            case "back":
                return RestoreBack();
            case "restore":
                if (args.Length < 2)
                {
                    return new CmdResult(false, "usage: wly restore <nodeIndex | nodeId>");
                }
                return RestoreNode(args[1]);
            case "run":
                return ListRunNodes();
            case "runrestore":
                if (args.Length < 2)
                {
                    return new CmdResult(false, "usage: wly runrestore <index>");
                }
                return RestoreRunNode(args[1]);
            case "step":
                if (args.Length < 3)
                {
                    return new CmdResult(false, "usage: wly step <bigNodeIndex> <stepIndex>");
                }
                return RestoreCombatStep(args[1], args[2]);
            case "dump":
                var path = Persistence.GraphExporter.Export(Graph, "manual");
                return new CmdResult(true, path == null ? "export failed" : "exported to " + path);
            case "outcome":
                return ListOutcomes();
            case "simreplay":
                return SimReplay(args);
            case "search":
                return RunSearch(args);
            case "searchstop":
                Search.AutoSearchService.StopRequested = true;
                return new CmdResult(true, "stop requested");
            case "simsearch":
                return RunSimSearch(args);
            case "validate":
                return RunValidate();
            case "optimal":
                return RunOptimal(args);
            case "weight":
                if (args.Length < 3)
                {
                    return new CmdResult(false, "usage: wly weight <hp|gold|potion|maxhp|relic> <0-5>");
                }
                if (!double.TryParse(args[2], out double w))
                {
                    return new CmdResult(false, "weight must be a number 0-5");
                }
                switch (args[1].ToLowerInvariant())
                {
                    case "hp": Objective.ObjectiveService.CurrentWeights.HpLost = w; break;
                    case "gold": Objective.ObjectiveService.CurrentWeights.Gold = w; break;
                    case "potion": Objective.ObjectiveService.CurrentWeights.Potion = w; break;
                    case "maxhp": Objective.ObjectiveService.CurrentWeights.MaxHp = w; break;
                    case "relic": Objective.ObjectiveService.CurrentWeights.Relic = w; break;
                    case "card": Objective.ObjectiveService.CurrentWeights.Card = w; break;
                    default: return new CmdResult(false, "unknown factor: " + args[1]);
                }
                return new CmdResult(true, $"weight {args[1]} = {w}");
            default:
                return new CmdResult(false, "unknown subcommand: " + args[0]);
        }
    }

    private static CmdResult ListOutcomes()
    {
        try
        {
            var graph = Graph;
            if (!graph.Nodes.Any(n => n.IsTerminal))
            {
                var archived = ModEntry.Recorder.ArchivedGraphs.LastOrDefault(g => g.Nodes.Any(n => n.IsTerminal));
                if (archived != null)
                {
                    graph = archived;
                }
            }
            Objective.ObjectiveService.RefreshScores(graph);
            var terminals = graph.Nodes.Where(n => n.IsTerminal && n.Outcome != null).ToList();
            if (terminals.Count == 0)
            {
                return new CmdResult(false, "no terminal nodes yet (combat not finished)");
            }
            var best = terminals.OrderByDescending(n => n.Outcome!.Score).First();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"combat={graph.Scope}");
            foreach (var n in terminals)
            {
                string mark = n == best ? "*" : " ";
                sb.AppendLine($"{mark} {n.Id.Substring(0, 8)} [{n.TerminalKind}] {n.Outcome!.Summary()} score={n.Outcome.Score:F2}");
            }
            sb.AppendLine("use 'wly weight <hp|gold|potion|maxhp|relic> <value>' to tune weights");
            return new CmdResult(true, sb.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            MegaCrit.Sts2.Core.Logging.Log.Error($"[WorldLineYggdrasil] wly outcome failed: {ex}");
            return new CmdResult(false, "outcome error: " + ex.Message);
        }
    }

    private static CmdResult RunSimSearch(string[] args)
    {
        var graph = ModEntry.Recorder.Graph;
        if (graph.RootNodeId == null)
        {
            return new CmdResult(false, "no combat graph (enter a combat first)");
        }
        int budget = 5000;
        if (args.Length > 1 && !int.TryParse(args[1], out budget))
        {
            return new CmdResult(false, "budget must be a number");
        }
        int beam = 5;
        if (args.Length > 2 && !int.TryParse(args[2], out beam))
        {
            return new CmdResult(false, "beam must be a number");
        }
        var root = graph.GetNode(graph.RootNodeId)!.Snapshot;
        var plan = Simulation.SimSearch.FindBest(root, graph.BossLevel, budget, beam);
        Simulation.SimSearch.LastPlan = plan;
        if (plan == null)
        {
            return new CmdResult(false, "no terminal found within budget");
        }
        return new CmdResult(true, $"sim best: [{plan.Kind}] score={plan.Score:F2} depth={plan.Depth}\nline: {plan.Line()}\n{plan.Outcome?.Summary()}");
    }

    private static CmdResult RunValidate()
    {
        var plan = Simulation.SimSearch.LastPlan;
        if (plan == null)
        {
            return new CmdResult(false, "no sim plan to validate (run wly simsearch first)");
        }
        var task = Validation.PlanValidator.Validate(plan);
        return new CmdResult(task, success: true, $"validating plan ({plan.Actions.Count} steps)...");
    }

    private static CmdResult RunOptimal(string[] args)
    {
        if (Search.OptimalPlayService.IsRunning)
        {
            return new CmdResult(false, "optimal already running");
        }
        int simBudget = 8000;
        if (args.Length > 1 && !int.TryParse(args[1], out simBudget))
        {
            return new CmdResult(false, "budget must be a number");
        }
        int fallbackBudget = 4000;
        if (args.Length > 2 && !int.TryParse(args[2], out fallbackBudget))
        {
            return new CmdResult(false, "fallback budget must be a number");
        }
        var task = Search.OptimalPlayService.RunOptimal(simBudget, fallbackBudget);
        return new CmdResult(task, success: true, $"running optimal play (sim {simBudget}, fallback {fallbackBudget})...");
    }

    private static CmdResult RunSearch(string[] args)
    {
        if (Search.AutoSearchService.IsSearching)
        {
            return new CmdResult(false, "search already running");
        }
        int budget = 2000;
        if (args.Length > 1 && !int.TryParse(args[1], out budget))
        {
            return new CmdResult(false, "budget must be a number");
        }
        bool fromRoot = args.Length > 2 && args[2].Equals("root", System.StringComparison.OrdinalIgnoreCase);
        var mode = Search.AutoSearchService.SearchMode.Beam;
        if (args.Length > 3)
        {
            mode = args[3].ToLowerInvariant() switch
            {
                "bfs" => Search.AutoSearchService.SearchMode.Bfs,
                "greedy" => Search.AutoSearchService.SearchMode.Greedy,
                _ => Search.AutoSearchService.SearchMode.Beam,
            };
        }
        Search.AutoSearchService.StopRequested = false;
        var task = Search.AutoSearchService.RunSearch(budget, fromRoot, mode);
        return new CmdResult(task, success: true, $"searching up to {budget} steps from {(fromRoot ? "root" : "current")} ({mode})...");
    }

    private static CmdResult SimReplay(string[] args)
    {
        // pick the combat graph to replay
        CombatGraph? graph = null;
        if (args.Length > 1 && int.TryParse(args[1], out int bi))
        {
            var bn = ModEntry.RunRecorder.Graph.Nodes.ToList().Skip(bi - 1).FirstOrDefault();
            if (bn?.CombatScope != null)
            {
                graph = ModEntry.Recorder.ArchivedGraphs.FirstOrDefault(g => g.Scope == bn.CombatScope);
            }
        }
        graph ??= Graph.Nodes.Any(n => n.IsTerminal) ? Graph
            : ModEntry.Recorder.ArchivedGraphs.LastOrDefault(g => g.Nodes.Any(n => n.IsTerminal));
        if (graph?.RootNodeId == null)
        {
            return new CmdResult(false, "no combat graph to replay (play a combat first)");
        }

        var sim = new Simulation.SimState();
        sim.CopyFrom(graph.GetNode(graph.RootNodeId)!.Snapshot);
        var cur = graph.RootNodeId;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"sim replay: {graph.Scope} start hp={sim.Hp}/{sim.MaxHp} e={sim.Energy}");

        int step = 0;
        var visited = new HashSet<string> { cur };
        while (true)
        {
            var edge = graph.Edges.FirstOrDefault(e => e.FromId == cur);
            if (edge == null)
            {
                break;
            }
            if (!visited.Add(edge.ToId))
            {
                sb.AppendLine("cycle detected; stopping replay");
                break;
            }
            var action = ActionFromEdge(sim, edge);
            if (action == null)
            {
                sb.AppendLine($"  step{step}: cannot reconstruct action from '{edge.Action}'");
                break;
            }
            Simulation.SimEngine.Apply(sim, action);
            var target = graph.GetNode(edge.ToId);
            bool hpOk = sim.Hp == target?.Snapshot.PlayerHp;
            bool eOk = sim.Energy == target?.Snapshot.Energy;
            sb.AppendLine($"  step{step}: {edge.Action} -> sim hp={sim.Hp} e={sim.Energy} blk={sim.Block} | recorded hp={target?.Snapshot.PlayerHp} e={target?.Snapshot.Energy} {(hpOk && eOk ? "OK" : "*** MISMATCH")}");
            cur = edge.ToId;
            step++;
            if (Simulation.SimEngine.IsTerminal(sim))
            {
                break;
            }
        }
        sb.AppendLine($"sim end: {Simulation.SimEngine.TerminalKind(sim)}");
        return new CmdResult(true, sb.ToString().TrimEnd());
    }

    private static Simulation.SimAction? ActionFromEdge(Simulation.SimState sim, GraphEdge edge)
    {
        var a = edge.Action;
        if (a == "end_turn")
        {
            return new Simulation.SimAction { Kind = Simulation.SimActionKind.EndTurn };
        }
        if (a.StartsWith("potion:"))
        {
            int idx = sim.Potions.FindIndex(p => p == a.Substring("potion:".Length));
            return new Simulation.SimAction { Kind = Simulation.SimActionKind.UsePotion, CardIndex = idx };
        }
        if (a.StartsWith("discard_potion:"))
        {
            int idx = sim.Potions.FindIndex(p => p == a.Substring("discard_potion:".Length));
            return new Simulation.SimAction { Kind = Simulation.SimActionKind.DiscardPotion, CardIndex = idx };
        }
        // card play
        int ci = sim.Hand.FindIndex(c => c == a);
        if (ci < 0)
        {
            return null;
        }
        var card = Simulation.CardDatabase.Get(a);
        if (card == null)
        {
            return null;
        }
        int ti = card.NeedsTarget ? 0 : -1;
        return new Simulation.SimAction { Kind = Simulation.SimActionKind.PlayCard, CardIndex = ci, TargetIndex = ti };
    }

    private static CmdResult ListRunNodes()
    {
        var rg = ModEntry.RunRecorder.Graph;
        if (rg.Nodes.Count == 0)
        {
            return new CmdResult(false, "no run graph yet");
        }
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"run tree: {rg.Nodes.Count} big nodes / {rg.Edges.Count} edges root={rg.RootNodeId}");
        var ordered = rg.Nodes.ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            var n = ordered[i];
            string mark = n.Id == rg.CurrentNodeId ? "*" : " ";
            string term = n.IsTerminal ? $" [{n.TerminalKind}]" : "";
            string combat = n.CombatScope != null ? $" combat={n.CombatScope}" : "";
            sb.AppendLine($"{mark}{i + 1}. {n.Id} {n.Label} floor={n.Floor}{combat}{term}");
        }
        sb.AppendLine("use: wly runrestore <n>  to jump the whole run back");
        return new CmdResult(true, sb.ToString().TrimEnd());
    }

    private static CmdResult RestoreRunNode(string arg)
    {
        var rg = ModEntry.RunRecorder.Graph;
        if (!int.TryParse(arg, out int idx))
        {
            return new CmdResult(false, "runrestore expects a 1-based index from 'wly run'");
        }
        var ordered = rg.Nodes.ToList();
        if (idx < 1 || idx > ordered.Count)
        {
            return new CmdResult(false, $"index {idx} out of range (1..{ordered.Count})");
        }
        var node = ordered[idx - 1];
        var task = Restore.RunRestoreService.RestoreTo(node);
        return new CmdResult(task, success: true, $"restoring run to {node.Label} (floor {node.Floor})");
    }

    private static CmdResult RestoreCombatStep(string bigIdx, string stepIdx)
    {
        // 小图功能已取消：不再支持跳转到过去战斗的局内小结点。
        return new CmdResult(false, "小图功能已移除；请用 wly runrestore 整局跳回后从头重打该战斗");
    }

    private static CombatGraph Graph => ModEntry.Recorder.Graph;

    private static List<GraphNode> OrderedNodes() => Graph.Nodes.ToList();

    private static CmdResult ListNodes()
    {
        var graph = Graph;
        if (graph.Nodes.Count == 0)
        {
            return new CmdResult(false, "no combat graph yet");
        }
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"combat scope={graph.Scope} nodes={graph.Nodes.Count} edges={graph.Edges.Count} root={graph.RootNodeId}");
        var ordered = OrderedNodes();
        for (int i = 0; i < ordered.Count; i++)
        {
            var n = ordered[i];
            var s = n.Snapshot;
            string tag = n.Id == graph.CurrentNodeId ? "*" : " ";
            string term = n.IsTerminal ? $" [{n.TerminalKind}]" : "";
            sb.AppendLine($"{tag}{i + 1}. {n.Id} hp={s.PlayerHp}/{s.PlayerMaxHp} t={s.TurnNumber} hand=[{string.Join(",", s.Hand)}]{term}");
        }
        sb.AppendLine("use: wly restore <n>  (n = line number above)");
        return new CmdResult(true, sb.ToString().TrimEnd());
    }

    private static CmdResult RestoreBack()
    {
        var edges = Graph.Edges;
        if (edges.Count == 0)
        {
            return new CmdResult(false, "no edges yet; nothing to rewind");
        }
        var current = Graph.CurrentNodeId;
        for (int i = edges.Count - 1; i >= 0; i--)
        {
            var e = edges[i];
            if (e.ToId == current)
            {
                return RestoreToNode(e.FromId);
            }
        }
        return new CmdResult(false, "current node has no recorded parent edge");
    }

    private static CmdResult RestoreNode(string arg)
    {
        // 1-based index?
        if (int.TryParse(arg, out int idx))
        {
            var ordered = OrderedNodes();
            if (idx >= 1 && idx <= ordered.Count)
            {
                return RestoreToNode(ordered[idx - 1].Id);
            }
            return new CmdResult(false, $"node index {idx} out of range (1..{ordered.Count})");
        }

        // full id or prefix
        var node = Graph.GetNode(arg);
        if (node == null)
        {
            node = Graph.Nodes.FirstOrDefault(n =>
                n.Id.StartsWith(arg, System.StringComparison.OrdinalIgnoreCase));
        }
        if (node == null)
        {
            return new CmdResult(false, $"node not found: {arg} (use 'wly nodes' to list)");
        }
        return RestoreToNode(node.Id);
    }

    private static CmdResult RestoreToNode(string nodeId)
    {
        bool ok = RestoreService.RestoreTo(nodeId);
        if (ok)
        {
            Graph.SetCurrent(nodeId);
            MegaCrit.Sts2.Core.Logging.Log.Info($"[WorldLineYggdrasil] restored to node {nodeId}");
            return new CmdResult(true, "restored to " + nodeId);
        }
        return new CmdResult(false, "restore failed (no deep snapshot for " + nodeId + ")");
    }
}