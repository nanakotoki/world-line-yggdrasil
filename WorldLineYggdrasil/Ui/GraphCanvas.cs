using Godot;
using WorldLineYggdrasil.Graph;
using GraphNode = WorldLineYggdrasil.Graph.GraphNode;

namespace WorldLineYggdrasil.Ui;

/// <summary>
/// Graph canvas rendered with BUILT-IN Godot nodes only (Panel / Label / Line2D),
/// so it works inside a runtime-loaded mod where custom Control override methods
/// (_Draw, _Ready, _GuiInput) are not wired by the engine. Input is handled via
/// the <c>gui_input</c> signal subscription.
/// </summary>
public sealed class GraphCanvas
{
    private readonly Control _world;
    private readonly Panel _bg;
    private readonly Label _empty;
    private readonly List<Node> _renderNodes = new();
    private readonly Dictionary<string, Vector2> _pos = new();
    private CombatGraph? _graph;
    private Vector2 _camera = new(-160, -40);
    private float _zoom = 0.9f;
    private bool _needsFit = true;
    private bool _userAdjustedView;
    private string? _hovered;
    private bool _panning;
    private Vector2 _lastMouse;

    // --- 圈点勾画 (freehand ink, world-space, survives rebuild) ---
    private bool _drawMode;
    private readonly Color _inkColor = new(1f, 0.85f, 0.2f, 0.95f);
    private readonly List<List<Vector2>> _strokes = new();
    private List<Vector2>? _curStroke;

    public bool DrawMode
    {
        get => _drawMode;
        set
        {
            if (_drawMode == value)
            {
                return;
            }
            _drawMode = value;
            if (!value)
            {
                EndStroke();
            }
        }
    }

    public bool HasInk => _strokes.Count > 0;
    public void ClearInk()
    {
        _strokes.Clear();
        EndStroke();
        Rebuild();
    }

    public Control Root { get; }
    public event Action<string>? NodeClicked;
    public event Action<string?>? NodeHovered;
    public string? HoveredNode => _hovered;
    private bool _renderLogged;

    public GraphCanvas()
    {
        Root = new Control
        {
            MouseFilter = Control.MouseFilterEnum.Stop,
            ClipContents = true,
            CustomMinimumSize = new Vector2(420, 300),
        };
        Root.GuiInput += OnInput;
        Root.Resized += OnResized;

        _bg = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
        _bg.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0.06f, 0.07f, 0.09f, 0.9f) });
        Root.AddChild(_bg);

        _world = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        Root.AddChild(_world);

        _empty = new Label
        {
            Text = Loc.T("empty_graph"),
            Position = new Vector2(14, 12),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        Root.AddChild(_empty);
    }

    public void SetGraph(CombatGraph? graph)
    {
        if (_graph == graph)
        {
            return;
        }
        if (_graph != null)
        {
            _graph.Changed -= OnGraphChanged;
        }
        _graph = graph;
        _needsFit = true;
        _userAdjustedView = false;
        _strokes.Clear();
        EndStroke();
        if (graph != null)
        {
            graph.Changed += OnGraphChanged;
        }
        Rebuild();
    }

    private void OnResized()
    {
        Rebuild();
    }

    private void OnGraphChanged()
    {
        // Coalesce rebuilds: auto-search can add many nodes per frame; rebuilding
        // the whole canvas on every single change thrashes the scene tree.
        if (_rebuildQueued)
        {
            return;
        }
        _rebuildQueued = true;
        Callable.From(() => { _rebuildQueued = false; Rebuild(); }).CallDeferred();
    }

    private bool _rebuildQueued;

    private void ComputeLayout()
    {
        _pos.Clear();
        if (_graph == null)
        {
            return;
        }
        var byTurn = new Dictionary<int, List<GraphNode>>();
        foreach (var n in _graph.Nodes)
        {
            int turn = n.Snapshot.TurnNumber;
            if (!byTurn.TryGetValue(turn, out var list))
            {
                byTurn[turn] = list = new List<GraphNode>();
            }
            list.Add(n);
        }
        foreach (var kv in byTurn.OrderBy(k => k.Key))
        {
            var nodes = kv.Value;
            int count = nodes.Count;
            float y = (kv.Key - 1) * 96f;
            float startX = -(count - 1) * 66f / 2f;
            for (int i = 0; i < count; i++)
            {
                _pos[nodes[i].Id] = new Vector2(startX + i * 66f, y);
            }
        }
    }

    private void AutoFit()
    {
        if (_userAdjustedView || _pos.Count == 0)
        {
            _needsFit = false;
            return;
        }
        var size = Root.Size;
        if (size.X < 60 || size.Y < 60)
        {
            return;
        }
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        foreach (var p in _pos.Values)
        {
            minX = Mathf.Min(minX, p.X); maxX = Mathf.Max(maxX, p.X);
            minY = Mathf.Min(minY, p.Y); maxY = Mathf.Max(maxY, p.Y);
        }
        float cx = (minX + maxX) / 2f, cy = (minY + maxY) / 2f;
        float spanX = Mathf.Max(120f, maxX - minX), spanY = Mathf.Max(120f, maxY - minY);
        _zoom = Mathf.Clamp(Mathf.Min((size.X - 40f) / spanX, (size.Y - 40f) / spanY), 0.25f, 2.5f);
        _camera = new Vector2(cx, cy);
        _needsFit = false;
    }

    private Vector2 WorldToScreen(Vector2 w)
    {
        var size = Root.Size;
        return (w - _camera) * _zoom + size / 2f;
    }

    private Vector2 ScreenToWorld(Vector2 s)
    {
        var size = Root.Size;
        return (s - size / 2f) / _zoom + _camera;
    }

    private void ClearRenderNodes()
    {
        foreach (var n in _renderNodes)
        {
            n.QueueFree();
        }
        _renderNodes.Clear();
    }

    public void Rebuild()
    {
        ClearRenderNodes();
        if (_graph == null)
        {
            _empty.Visible = true;
            return;
        }
        ComputeLayout();
        if (_needsFit)
        {
            AutoFit();
        }
        if (_pos.Count == 0)
        {
            _empty.Visible = true;
            return;
        }
        _empty.Visible = false;

        var size = Root.Size;
        var font = ThemeDB.FallbackFont;

        // background fill
        _bg.Position = Vector2.Zero;
        _bg.Size = size;

        // turn separators
        foreach (var y in _pos.Values.Select(p => p.Y).Distinct().OrderBy(v => v))
        {
            var sy = WorldToScreen(new Vector2(0, y));
            var line = new Line2D { Points = new Vector2[] { new(0, sy.Y), new(size.X, sy.Y) }, Width = 1f, DefaultColor = new Color(1f, 1f, 1f, 0.06f) };
            _world.AddChild(line);
            _renderNodes.Add(line);
            var label = new Label { Text = $"第{(int)(y / 96f + 1)}回合", Position = new Vector2(6, sy.Y - 18), Modulate = new Color(0.85f, 0.87f, 0.9f, 0.5f), MouseFilter = Control.MouseFilterEnum.Ignore };
            _world.AddChild(label);
            _renderNodes.Add(label);
        }

        // edges
        foreach (var e in _graph.Edges)
        {
            if (!_pos.TryGetValue(e.FromId, out var a) || !_pos.TryGetValue(e.ToId, out var b))
            {
                continue;
            }
            var sa = WorldToScreen(a);
            var sb = WorldToScreen(b);
            if (e.FromId == e.ToId)
            {
                continue; // self-loop skipped for v2 (would need arc)
            }
            // best-path edges highlighted gold
            bool bestEdge = _graph.BestPathNodes.Contains(e.FromId) && _graph.BestPathNodes.Contains(e.ToId);
            var line = new Line2D { Points = new Vector2[] { sa, sb }, Width = bestEdge ? 3f : 1.6f, DefaultColor = bestEdge ? new Color(1f, 0.85f, 0.3f) : new Color(0.42f, 0.48f, 0.56f) };
            _world.AddChild(line);
            _renderNodes.Add(line);

            // arrowhead
            var dir = (sb - sa).Normalized();
            var tip = sb - dir * 10f;
            var nrm = new Vector2(-dir.Y, dir.X);
            var tri = new Polygon2D
            {
                Polygon = new Vector2[] { tip + dir * 7f, tip - nrm * 4f, tip + nrm * 4f },
                Color = bestEdge ? new Color(1f, 0.85f, 0.3f) : new Color(0.5f, 0.56f, 0.65f),
            };
            _world.AddChild(tri);
            _renderNodes.Add(tri);
        }

        // freehand ink annotations
        foreach (var stroke in _strokes)
        {
            if (stroke.Count < 2)
            {
                continue;
            }
            var pts = stroke.Select(p => WorldToScreen(p)).ToArray();
            var ink = new Line2D
            {
                Points = pts,
                Width = 2.2f,
                DefaultColor = _inkColor,
                Antialiased = true,
            };
            _world.AddChild(ink);
            _renderNodes.Add(ink);
        }
        if (_curStroke is { Count: >= 2 })
        {
            var pts = _curStroke.Select(p => WorldToScreen(p)).ToArray();
            var ink = new Line2D { Points = pts, Width = 2.2f, DefaultColor = _inkColor, Antialiased = true };
            _world.AddChild(ink);
            _renderNodes.Add(ink);
        }

        // nodes
        if (!_renderLogged)
        {
            _renderLogged = true;
            MegaCrit.Sts2.Core.Logging.Log.Info($"[WorldLineYggdrasil.Ui] canvas rebuild: nodes={_graph.Nodes.Count} edges={_graph.Edges.Count} size={Root.Size}");
        }
        foreach (var n in _graph.Nodes)
        {
            if (!_pos.TryGetValue(n.Id, out var p))
            {
                continue;
            }
            var sp = WorldToScreen(p);
            float r = n.IsTerminal ? 13f : 9f;
            var col = NodeColor(n);

            var panel = new Panel { Position = sp - new Vector2(r, r), Size = new Vector2(r * 2, r * 2), MouseFilter = Control.MouseFilterEnum.Ignore };
            panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = col,
                CornerRadiusTopLeft = (int)r,
                CornerRadiusTopRight = (int)r,
                CornerRadiusBottomLeft = (int)r,
                CornerRadiusBottomRight = (int)r,
            });
            _world.AddChild(panel);
            _renderNodes.Add(panel);

            if (n.Id == _graph.CurrentNodeId)
            {
                var ring = new Panel { Position = sp - new Vector2(r + 4, r + 4), Size = new Vector2((r + 4) * 2, (r + 4) * 2), MouseFilter = Control.MouseFilterEnum.Ignore };
                ring.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = Colors.Transparent, BorderColor = new Color(0.85f, 0.62f, 0.93f), BorderWidthLeft = 3, BorderWidthRight = 3, BorderWidthTop = 3, BorderWidthBottom = 3, CornerRadiusTopLeft = (int)(r + 4), CornerRadiusTopRight = (int)(r + 4), CornerRadiusBottomLeft = (int)(r + 4), CornerRadiusBottomRight = (int)(r + 4) });
                _world.AddChild(ring);
                _renderNodes.Add(ring);
            }
            else if (_graph.BestPathNodes.Contains(n.Id))
            {
                var ring = new Panel { Position = sp - new Vector2(r + 3, r + 3), Size = new Vector2((r + 3) * 2, (r + 3) * 2), MouseFilter = Control.MouseFilterEnum.Ignore };
                ring.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = Colors.Transparent, BorderColor = new Color(1f, 0.85f, 0.3f, 0.9f), BorderWidthLeft = 2, BorderWidthRight = 2, BorderWidthTop = 2, BorderWidthBottom = 2, CornerRadiusTopLeft = (int)(r + 3), CornerRadiusTopRight = (int)(r + 3), CornerRadiusBottomLeft = (int)(r + 3), CornerRadiusBottomRight = (int)(r + 3) });
                _world.AddChild(ring);
                _renderNodes.Add(ring);
            }

            // labels: only on root / current / hovered / terminal nodes so the
            // graph stays clean; dark background keeps the text readable.
            bool keyNode = n.Id == _graph.RootNodeId || n.Id == _graph.CurrentNodeId || n.Id == _hovered || n.IsTerminal;
            if (keyNode && sp.X >= -20 && sp.X <= size.X + 20 && sp.Y >= -20 && sp.Y <= size.Y + 20)
            {
                string name = NodeLabel(n);
                var nameLabel = new Label { Text = name, Position = sp + new Vector2(-40, -r - 20), Size = new Vector2(80, 16), HorizontalAlignment = HorizontalAlignment.Center, Modulate = new Color(0.92f, 0.94f, 0.97f), MouseFilter = Control.MouseFilterEnum.Ignore };
                nameLabel.AddThemeStyleboxOverride("normal", new StyleBoxFlat { BgColor = new Color(0.05f, 0.06f, 0.08f, 0.85f), ContentMarginLeft = 4, ContentMarginRight = 4 });
                _world.AddChild(nameLabel);
                _renderNodes.Add(nameLabel);

                var hp = n.Snapshot;
                var hpLabel = new Label { Text = $"HP {hp.PlayerHp}/{hp.PlayerMaxHp}", Position = sp + new Vector2(-40, r + 4), Size = new Vector2(80, 16), HorizontalAlignment = HorizontalAlignment.Center, Modulate = new Color(0.88f, 0.92f, 0.88f), MouseFilter = Control.MouseFilterEnum.Ignore };
                hpLabel.AddThemeStyleboxOverride("normal", new StyleBoxFlat { BgColor = new Color(0.05f, 0.06f, 0.08f, 0.85f), ContentMarginLeft = 4, ContentMarginRight = 4 });
                _world.AddChild(hpLabel);
                _renderNodes.Add(hpLabel);
            }
        }
    }

    private string NodeLabel(GraphNode n)
    {
        if (!string.IsNullOrEmpty(n.Name))
        {
            return n.Name;
        }
        if (n.Id == _graph!.RootNodeId)
        {
            return "起点";
        }
        foreach (var e in _graph.Edges)
        {
            if (e.ToId == n.Id)
            {
                return e.Action;
            }
        }
        return "?";
    }

    private Color NodeColor(GraphNode n)
    {
        if (n.Id == _graph!.RootNodeId)
        {
            return new Color(0.30f, 0.72f, 0.35f);
        }
        if (n.IsTerminal)
        {
            return n.TerminalKind == "Defeat" ? new Color(0.91f, 0.35f, 0.32f) : new Color(1f, 0.84f, 0.31f);
        }
        var s = n.Snapshot;
        float frac = s.PlayerMaxHp > 0 ? (float)s.PlayerHp / s.PlayerMaxHp : 1f;
        return frac >= 0.7f ? new Color(0.39f, 0.62f, 0.9f)
            : frac >= 0.4f ? new Color(0.95f, 0.75f, 0.30f)
            : new Color(0.9f, 0.45f, 0.38f);
    }

    private void OnInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton mb:
                if (mb.ButtonIndex == MouseButton.WheelUp)
                {
                    _zoom = Mathf.Clamp(_zoom * 1.15f, 0.25f, 4f);
                    _userAdjustedView = true;
                    Rebuild();
                    Root.AcceptEvent();
                }
                else if (mb.ButtonIndex == MouseButton.WheelDown)
                {
                    _zoom = Mathf.Clamp(_zoom * 0.87f, 0.25f, 4f);
                    _userAdjustedView = true;
                    Rebuild();
                    Root.AcceptEvent();
                }
                else if (mb.ButtonIndex == MouseButton.Left)
                {
                    if (mb.Pressed)
                    {
                        if (_drawMode)
                        {
                            _curStroke = new List<Vector2> { ScreenToWorld(mb.Position) };
                        }
                        else
                        {
                            var hit = HitTest(mb.Position);
                            if (hit != null)
                            {
                                _hovered = hit;
                                NodeClicked?.Invoke(hit);
                            }
                        }
                    }
                    else if (_curStroke != null)
                    {
                        _curStroke.Add(ScreenToWorld(mb.Position));
                        EndStroke();
                    }
                    Root.AcceptEvent();
                }
                else if (mb.ButtonIndex == MouseButton.Right)
                {
                    _panning = mb.Pressed;
                    _lastMouse = mb.Position;
                    Root.AcceptEvent();
                }
                break;

            case InputEventMouseMotion mm:
                if (_panning)
                {
                    _camera -= (mm.Position - _lastMouse) / _zoom;
                    _lastMouse = mm.Position;
                    _userAdjustedView = true;
                    Rebuild();
                }
                else if (_drawMode && _curStroke != null)
                {
                    var w = ScreenToWorld(mm.Position);
                    var last = _curStroke[_curStroke.Count - 1];
                    if ((w - last).Length() > 4f / _zoom)
                    {
                        _curStroke.Add(w);
                        Rebuild();
                    }
                }
                else
                {
                    var h = HitTest(mm.Position);
                    if (h != _hovered)
                    {
                        _hovered = h;
                        NodeHovered?.Invoke(h);
                    }
                }
                break;
        }
    }

    private string? HitTest(Vector2 screen)
    {
        var world = ScreenToWorld(screen);
        string? best = null;
        float bestD = 22f;
        foreach (var kv in _pos)
        {
            float d = (kv.Value - world).Length();
            if (d < bestD)
            {
                bestD = d;
                best = kv.Key;
            }
        }
        return best;
    }

    private void EndStroke()
    {
        if (_curStroke != null)
        {
            // keep strokes with at least 2 points; a lone click is discarded
            if (_curStroke.Count >= 2)
            {
                _strokes.Add(_curStroke);
            }
            _curStroke = null;
            Rebuild();
        }
    }
}