using WorldLineYggdrasil.Graph;

namespace WorldLineYggdrasil.Simulation;

public enum SimPlanActionKind
{
    PlayCard,
    EndTurn,
    UsePotion,
    DiscardPotion,
}

/// <summary>A portable action in a simulated plan (card id + target), replayable
/// in the real game by resolving the card id in the live hand.</summary>
public sealed class SimPlanAction
{
    public SimPlanActionKind Kind { get; init; }
    public string CardId { get; init; } = "";
    public int TargetIndex { get; init; } = -1;

    public override string ToString() => Kind switch
    {
        SimPlanActionKind.PlayCard => $"{CardId}->t{TargetIndex}",
        SimPlanActionKind.EndTurn => "end_turn",
        SimPlanActionKind.UsePotion => "use:" + CardId,
        SimPlanActionKind.DiscardPotion => "discard:" + CardId,
        _ => "?",
    };
}

public sealed class SimPlan
{
    public List<SimPlanAction> Actions { get; set; } = new();
    public string Kind { get; set; } = "";
    public double Score { get; set; }
    public Objective.Outcome? Outcome { get; set; }
    public int Depth { get; set; }

    public string Line()
    {
        return string.Join(" > ", Actions);
    }
}

/// <summary>
/// Offline optimal search over the simulator. The simulator is not bound by the
/// live combat's "ends on win" constraint, so it can collect every terminal it
/// reaches within budget and rank them by the M3 objective.
/// </summary>
public static class SimSearch
{
    /// <summary>Most recently found plan (for validation / replay).</summary>
    public static SimPlan? LastPlan { get; set; }

    /// <summary>Finds the best terminal reachable within budget (beam search).</summary>
    public static SimPlan? FindBest(CombatSnapshotData root, int bossLevel, int nodeBudget, int beamWidth = 5)
    {
        SimSetup.Configure();
        var start = new SimState().CopyFrom(root);
        start.ShuffleRng = RuntimeCardData.CaptureShuffleRng();
        start.MonsterRng = RuntimeCardData.CaptureMonsterRng();
        var terminals = new List<(SimState state, List<SimPlanAction> path)>();

        var beam = new List<(SimState state, List<SimPlanAction> path, double h)>();
        beam.Add((start, new List<SimPlanAction>(), Heuristic(start)));
        int nodes = 0;

        while (beam.Count > 0 && nodes < nodeBudget)
        {
            var next = new List<(SimState state, List<SimPlanAction> path, double h)>();
            foreach (var (state, path, _) in beam)
            {
                if (SimEngine.IsTerminal(state))
                {
                    terminals.Add((state, path));
                    continue;
                }
                var actions = SimEngine.EnumActions(state);
                foreach (var action in actions)
                {
                    if (nodes >= nodeBudget)
                    {
                        break;
                    }
                    string cardId = ResolveCardId(state, action);
                    var child = state.Clone();
                    SimEngine.Apply(child, action);
                    nodes++;

                    var newPath = new List<SimPlanAction>(path);
                    if (cardId != "" || action.Kind == SimActionKind.EndTurn)
                    {
                        newPath.Add(ToPlanAction(action, cardId));
                    }
                    if (SimEngine.IsTerminal(child))
                    {
                        terminals.Add((child, newPath));
                    }
                    else
                    {
                        next.Add((child, newPath, Heuristic(child)));
                    }
                }
            }
            beam = next.OrderByDescending(n => n.h).Take(beamWidth).ToList();
            if (beam.Count == 0)
            {
                break;
            }
        }

        SimPlan? best = null;
        foreach (var (state, path) in terminals)
        {
            var snap = state.ToSnapshot();
            var outcome = Objective.ObjectiveService.ComputeOutcome(root, snap, bossLevel);
            var plan = new SimPlan
            {
                Actions = path,
                Kind = SimEngine.TerminalKind(state),
                Score = outcome.Score,
                Outcome = outcome,
                Depth = path.Count,
            };
            if (best == null || plan.Score > best.Score)
            {
                best = plan;
            }
        }
        return best;
    }

    private static SimPlanAction ToPlanAction(SimAction action, string cardId)
    {
        return new SimPlanAction
        {
            Kind = action.Kind switch
            {
                SimActionKind.PlayCard => SimPlanActionKind.PlayCard,
                SimActionKind.EndTurn => SimPlanActionKind.EndTurn,
                SimActionKind.UsePotion => SimPlanActionKind.UsePotion,
                _ => SimPlanActionKind.DiscardPotion,
            },
            CardId = cardId,
            TargetIndex = action.TargetIndex,
        };
    }

    private static string ResolveCardId(SimState s, SimAction a)
    {
        return a.Kind switch
        {
            SimActionKind.PlayCard => a.CardIndex >= 0 && a.CardIndex < s.Hand.Count ? s.Hand[a.CardIndex] : "",
            SimActionKind.UsePotion or SimActionKind.DiscardPotion => a.CardIndex >= 0 && a.CardIndex < s.Potions.Count ? s.Potions[a.CardIndex] : "",
            _ => "",
        };
    }

    private static double Heuristic(SimState s)
    {
        double enemyHp = s.Enemies.Sum(e => Math.Max(0, e.Hp));
        return s.Hp - enemyHp * 0.05;
    }
}