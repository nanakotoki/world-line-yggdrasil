namespace WorldLineYggdrasil.Graph;

/// <summary>
/// In-memory directed graph for one combat. Nodes are game states, edges are
/// player actions. Cycles and self-loops are expected and stored as-is.
/// </summary>
public sealed class CombatGraph
{
    private readonly Dictionary<string, GraphNode> _nodes = new();
    private readonly List<GraphEdge> _edges = new();
    private readonly HashSet<string> _edgeKeys = new();

    public string Scope { get; private set; } = "";
    public string? RootNodeId { get; private set; }
    public string? CurrentNodeId { get; private set; }

    /// <summary>0 = normal combat; 1/2/3 = which act's boss this is.</summary>
    public int BossLevel { get; set; }
    public IReadOnlyList<GraphNode> Nodes => _nodes.Values.ToList();
    public IReadOnlyList<GraphEdge> Edges => _edges;

    public event Action? Changed;

    public void Reset(string scope)
    {
        _nodes.Clear();
        _edges.Clear();
        _edgeKeys.Clear();
        Scope = scope;
        RootNodeId = null;
        CurrentNodeId = null;
        Changed?.Invoke();
    }

    public GraphNode? GetNode(string id) => _nodes.TryGetValue(id, out var n) ? n : null;

    public GraphNode AddOrUpdateNode(CombatSnapshotData snapshot, bool isTerminal, string? terminalKind)
    {
        string id = Canonicalizer.Fingerprint(snapshot);
        if (!_nodes.TryGetValue(id, out var node))
        {
            node = new GraphNode
            {
                Id = id,
                Canonical = Canonicalizer.CanonicalString(snapshot),
                Snapshot = snapshot,
            };
            _nodes.Add(id, node);
            if (RootNodeId == null)
            {
                RootNodeId = id;
            }
        }
        if (isTerminal)
        {
            node.IsTerminal = true;
            node.TerminalKind = terminalKind;
        }
        node.MergeCount++;
        return node;
    }

    /// <summary>
    /// Adds an edge from->to labelled with the action, deduplicating identical
    /// (from, action, to) triples. Self-loops and repeated edges to visited nodes
    /// (cycles) are recorded.
    /// </summary>
    public GraphEdge? AddEdge(string fromId, string toId, string action, uint? targetId)
    {
        string key = $"{fromId}|{action}|{toId}|{targetId?.ToString() ?? "-"}";
        if (!_edgeKeys.Add(key))
        {
            return null;
        }
        var edge = new GraphEdge { FromId = fromId, ToId = toId, Action = action, TargetId = targetId };
        _edges.Add(edge);
        return edge;
    }

    public void SetCurrent(string nodeId)
    {
        CurrentNodeId = nodeId;
        Changed?.Invoke();
    }

    /// <summary>Nodes on the best (highest-scoring) path found by auto-search.</summary>
    public HashSet<string> BestPathNodes { get; } = new();
    public string? BestTerminalId { get; set; }

    /// <summary>Walks backward from a terminal to the root and marks the best path.</summary>
    public void SetBestPath(string terminalId)
    {
        BestPathNodes.Clear();
        BestTerminalId = terminalId;
        string? cur = terminalId;
        while (cur != null)
        {
            BestPathNodes.Add(cur);
            var edge = _edges.LastOrDefault(e => e.ToId == cur);
            cur = edge?.FromId;
        }
        Changed?.Invoke();
    }

    public void NotifyChanged() => Changed?.Invoke();

    /// <summary>
    /// Marks the given node as terminal (combat ended there).
    /// </summary>
    public void MarkTerminal(string nodeId, string kind)
    {
        if (_nodes.TryGetValue(nodeId, out var node))
        {
            node.IsTerminal = true;
            node.TerminalKind = kind;
            Changed?.Invoke();
        }
    }
}

public sealed class GraphNode
{
    public string Id { get; set; } = "";
    public string Canonical { get; set; } = "";
    public string? Name { get; set; }
    public CombatSnapshotData Snapshot { get; set; } = new();
    public bool IsTerminal { get; set; }
    public string? TerminalKind { get; set; }
    public int MergeCount { get; set; }

    /// <summary>Out-of-combat gains vs the root, when this node is terminal.</summary>
    public Objective.Outcome? Outcome { get; set; }
}

public sealed class GraphEdge
{
    public string FromId { get; set; } = "";
    public string ToId { get; set; } = "";
    public string Action { get; set; } = "";
    public uint? TargetId { get; set; }
}