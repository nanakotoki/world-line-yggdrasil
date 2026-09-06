using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Logging;
using WorldLineYggdrasil.Simulation;
using WorldLineYggdrasil.Validation;

namespace WorldLineYggdrasil.Search;

/// <summary>
/// The "最优化打法" pipeline (no UI): from the player's current combat node,
/// 1) run the offline simulator beam search for the best line by M3 outcome;
/// 2) execute that line in the REAL game (fast), validating each step against
///    the simulator via the existing restore/inject machinery;
/// 3) if every step matches to a terminal, the "optimal" line is real-validated;
///    otherwise hand off to the real-game beam search from the last real-valid
///    node (guaranteed-correct states) to finish the fight.
/// The player can afterwards jump anywhere via the run-level SL graph.
/// </summary>
public static class OptimalPlayService
{
    public static bool IsRunning { get; private set; }

    public static async Task RunOptimal(int simBudget = 8000, int fallbackBudget = 4000)
    {
        if (IsRunning)
        {
            return;
        }
        IsRunning = true;
        try
        {
            SearchSpeed.Begin();

            var graph = ModEntry.Recorder.Graph;
            string start = graph.CurrentNodeId ?? graph.RootNodeId ?? "";
            if (start == "")
            {
                Log.Info("[WorldLineYggdrasil.Optimal] no combat graph to plan from");
                return;
            }

            Log.Info($"[WorldLineYggdrasil.Optimal] planning from node {start} (sim budget {simBudget})...");
            var root = graph.GetNode(start)!.Snapshot;
            var plan = Simulation.SimSearch.FindBest(root, graph.BossLevel, simBudget);
            Simulation.SimSearch.LastPlan = plan;

            if (plan == null || plan.Actions.Count == 0)
            {
                Log.Info("[WorldLineYggdrasil.Optimal] sim found no terminal; falling back to real-game search");
                await Search.AutoSearchService.RunSearch(fallbackBudget, fromRoot: false, Search.AutoSearchService.SearchMode.Beam);
                return;
            }

            Log.Info($"[WorldLineYggdrasil.Optimal] sim candidate: [{plan.Kind}] score={plan.Score:F2} depth={plan.Depth}");
            Log.Info($"[WorldLineYggdrasil.Optimal] line: {plan.Line()}");

            var result = await Validation.PlanValidator.Validate(plan);
            if (result.Valid)
            {
                string oneLiner = plan.Outcome != null
                    ? $"最优打法: [{plan.Kind}] {plan.Outcome.Summary()} (score {plan.Score:F2}) —— 已在本场真实战斗中验证成立"
                    : $"最优打法: [{plan.Kind}] score {plan.Score:F2} —— 已在本场真实战斗中验证成立";
                Log.Info($"[WorldLineYggdrasil.Optimal] {oneLiner}");
                Log.Info($"[WorldLineYggdrasil.Optimal] 打法: {plan.Line()}");
                return;
            }

            Log.Info($"[WorldLineYggdrasil.Optimal] sim diverged at step {result.DivergedAtStep} ({result.Message})");
            var done = ReportIfCombatAlreadyEnded();
            if (!done)
            {
                Log.Info("[WorldLineYggdrasil.Optimal] restarting the real-game beam search from the combat root for a real-valid line");
                await Search.AutoSearchService.RunSearch(fallbackBudget, fromRoot: true, Search.AutoSearchService.SearchMode.Beam);
                LogBestFromGraph();
            }
        }
        finally
        {
            SearchSpeed.End();
            IsRunning = false;
        }
    }

    /// <summary>The validation may have actually WON the combat (the sim under-planned
    /// and the real ended before the plan's last actions). If the graph now holds a
    /// terminal, report it as the real outcome instead of re-searching.</summary>
    private static bool ReportIfCombatAlreadyEnded()
    {
        var graph = ModEntry.Recorder.Graph;
        var terminals = graph.Nodes.Where(n => n.IsTerminal).ToList();
        if (terminals.Count == 0)
        {
            return false;
        }
        var victory = terminals.FirstOrDefault(t => t.TerminalKind == "Victory");
        if (victory != null)
        {
            var o = victory.Outcome;
            Log.Info($"[WorldLineYggdrasil.Optimal] 最优打法实际已在验证中获胜: [Victory] 战损-{(o?.HpLost ?? 0)} (score {(o?.Score ?? 0):F2})（sim 计划偏长，真实提前结束）");
            return true;
        }
        var defeat = terminals.FirstOrDefault();
        Log.Info($"[WorldLineYggdrasil.Optimal] 验证中战斗结束: [{defeat?.TerminalKind ?? "?"}]（未能获胜，尝试兜底已无意义）");
        return true;
    }

    /// <summary>Reconstructs the root->best-terminal action line from the graph
    /// (after the real-game beam marked its best path) and logs one readable
    /// "最优化打法" conclusion.</summary>
    private static void LogBestFromGraph()
    {
        var graph = ModEntry.Recorder.Graph;
        if (graph.BestTerminalId == null || graph.RootNodeId == null)
        {
            Log.Info("[WorldLineYggdrasil.Optimal] real-game fallback found no winning terminal");
            return;
        }
        var best = graph.GetNode(graph.BestTerminalId);
        var steps = new List<string>();
        string? cur = graph.RootNodeId;
        var seen = new HashSet<string>();
        while (cur != null && cur != graph.BestTerminalId && seen.Add(cur))
        {
            var edge = graph.Edges.FirstOrDefault(e => e.FromId == cur && graph.BestPathNodes.Contains(e.ToId));
            if (edge == null)
            {
                break;
            }
            steps.Add(edge.Action);
            cur = edge.ToId;
        }
        var kind = best?.TerminalKind ?? "?";
        var outcome = best?.Outcome;
        string line = steps.Count > 0 ? string.Join(" > ", steps) : "(path not reconstructed)";
        Log.Info($"[WorldLineYggdrasil.Optimal] 最优打法: [{kind}] 战损-{(outcome?.HpLost ?? 0)} (score {(outcome?.Score ?? 0):F2})");
        Log.Info($"[WorldLineYggdrasil.Optimal] 打法: {line}");
    }
}