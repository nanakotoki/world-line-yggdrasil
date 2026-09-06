using MegaCrit.Sts2.Core.Saves;

namespace WorldLineYggdrasil.Graph;

/// <summary>
/// The run-level "big node" tree: rooms / events as big nodes, choices as
/// edges, combat graphs hanging off combat big nodes as "small node" subgraphs.
/// Branches appear when the player restores to an earlier big node and takes a
/// different path (world lines).
/// </summary>
public sealed class RunGraph
{
    private readonly Dictionary<string, RunNode> _nodes = new();
    private readonly List<RunEdge> _edges = new();

    public string Seed { get; set; } = "";
    public string? RootNodeId { get; private set; }
    public string? CurrentNodeId { get; private set; }
    public IReadOnlyList<RunNode> Nodes => _nodes.Values.ToList();
    public IReadOnlyList<RunEdge> Edges => _edges;

    public event Action? Changed;

    public void Reset(string seed)
    {
        _nodes.Clear();
        _edges.Clear();
        Seed = seed;
        RootNodeId = null;
        CurrentNodeId = null;
        Changed?.Invoke();
    }

    public RunNode? GetNode(string id) => _nodes.TryGetValue(id, out var n) ? n : null;

    public RunNode AddOrUpdateNode(RunNode node)
    {
        if (_nodes.TryGetValue(node.Id, out var existing))
        {
            existing.Label = node.Label;
            existing.Floor = node.Floor;
            existing.RoomType = node.RoomType;
            // The run save is the "存档点": it must stay IMMUTABLE once captured.
            // Overwriting it on re-entry bakes in post-restore drift (advanced RNG),
            // so a later LoadRun re-rolls a DIFFERENT encounter and corrupts the
            // whole SL / past-combat restore chain.
            if (existing.Save == null && node.Save != null)
            {
                existing.Save = node.Save;
            }
            return existing;
        }
        _nodes[node.Id] = node;
        if (RootNodeId == null)
        {
            RootNodeId = node.Id;
        }
        return node;
    }

    public RunEdge? AddEdge(string fromId, string toId, string action)
    {
        if (_nodes.ContainsKey(fromId) && _nodes.ContainsKey(toId))
        {
            var e = new RunEdge { FromId = fromId, ToId = toId, Action = action };
            _edges.Add(e);
            return e;
        }
        return null;
    }

    public void SetCurrent(string nodeId)
    {
        CurrentNodeId = nodeId;
        Changed?.Invoke();
    }

    public void NotifyChanged() => Changed?.Invoke();

    /// <summary>Children of a node (edges originating from it), ordered.</summary>
    public IReadOnlyList<RunEdge> ChildrenOf(string nodeId) => _edges.Where(e => e.FromId == nodeId).ToList();
}

public sealed class RunNode
{
    public string Id { get; set; } = "";
    public string RoomType { get; set; } = "";
    public string Label { get; set; } = "";
    public int Floor { get; set; }
    public string? CombatScope { get; set; }
    public SerializableRun? Save { get; set; }
    public bool IsTerminal { get; set; }
    public string? TerminalKind { get; set; }
}

public sealed class RunEdge
{
    public string FromId { get; set; } = "";
    public string ToId { get; set; } = "";
    public string Action { get; set; } = "";
}