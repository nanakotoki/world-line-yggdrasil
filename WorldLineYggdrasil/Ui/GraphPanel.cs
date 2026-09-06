using Godot;
using WorldLineYggdrasil.Capture;
using WorldLineYggdrasil.Graph;

namespace WorldLineYggdrasil.Ui;

/// <summary>
/// The in-game graph panel with two views:
///   - 战斗图: the current combat's state graph canvas (click node to restore).
///   - 运行图: the run-level big-node tree (rooms/events); click a big node to
///     restore the whole run to it, or open a linked combat sub-graph.
/// </summary>
public static class GraphPanel
{
    private static Control? _root;
    private static GraphCanvas? _canvas;
    private static Label? _counts;
    private static Label? _currentNode;
    private static Label? _hoverNode;
    private static LineEdit? _nameEdit;
    private static Control? _combatView;
    private static Control? _runView;
    private static ScrollContainer? _runScroll;
    private static VBoxContainer? _runList;
    private static Button? _tabCombat;
    private static Button? _tabRun;
    private static string _view = "combat";
    private static CombatGraph? _boundGraph;
    private static int _lastVersion = -1;
    private static Label? _outcomeLabel;
    private static Label? _searchStatus;
    private static int _lastRunVersion = -1;
    private static string? _selectedId;
    private static bool _rebuilding;

    public static Control Build()
    {
        _root = new Control
        {
            Name = "WlyPanel",
            MouseFilter = Control.MouseFilterEnum.Stop,
            ClipContents = true,
        };
        _root.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        _root.GrowHorizontal = Control.GrowDirection.Begin;
        _root.AnchorLeft = 1;
        _root.AnchorRight = 1;
        _root.AnchorTop = 0;
        _root.AnchorBottom = 1;
        _root.OffsetLeft = -680;
        _root.OffsetRight = 0;
        _root.OffsetTop = 0;
        _root.OffsetBottom = 0;
        _root.CustomMinimumSize = new Vector2(680, 0);

        var chrome = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop };
        chrome.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        chrome.AddThemeStyleboxOverride("panel", PanelStyle());

        var layout = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };

        // header
        var header = new HBoxContainer();
        var title = new Label { Text = Loc.T("panel_title"), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _counts = new Label { Text = "" };
        var close = new Button { Text = "×", FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(28, 28) };
        close.Pressed += () => { if (_root != null) _root.Visible = false; };
        header.AddChild(title);
        header.AddChild(_counts);
        header.AddChild(close);
        layout.AddChild(header);

        // view tabs
        var tabs = new HBoxContainer();
        _tabCombat = new Button { Text = Loc.T("tab_combat"), ToggleMode = true, ButtonPressed = true, FocusMode = Control.FocusModeEnum.None };
        _tabRun = new Button { Text = Loc.T("tab_run"), ToggleMode = true, FocusMode = Control.FocusModeEnum.None };
        _tabCombat.Pressed += () => SetView("combat");
        _tabRun.Pressed += () => SetView("run");
        tabs.AddChild(_tabCombat);
        tabs.AddChild(_tabRun);
        layout.AddChild(tabs);

        // combat view
        _combatView = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _combatView.AddChild(new Label
        {
            Text = Loc.T("hint_ops"),
            Modulate = new Color(0.7f, 0.72f, 0.76f),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        });
        _canvas = new GraphCanvas();
        _canvas.Root.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _canvas.Root.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _canvas.SetGraph(ModEntry.Recorder.Graph);
        _canvas.NodeClicked += OnNodeClicked;
        _canvas.NodeHovered += OnNodeHovered;
        _combatView.AddChild(_canvas.Root);

        var footer = new HBoxContainer();
        _hoverNode = new Label { Text = Loc.T("lbl_unspecified"), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        _nameEdit = new LineEdit { PlaceholderText = Loc.T("name_placeholder"), CustomMinimumSize = new Vector2(140, 30) };
        var nameBtn = new Button { Text = Loc.T("btn_name"), FocusMode = Control.FocusModeEnum.None };
        nameBtn.Pressed += ApplyName;
        var drawBtn = new Button { Text = Loc.T("btn_draw"), ToggleMode = true, FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(60, 30) };
        drawBtn.Pressed += () => { _canvas!.DrawMode = drawBtn.ButtonPressed; drawBtn.Text = drawBtn.ButtonPressed ? Loc.T("btn_select") : Loc.T("btn_draw"); };
        var clearInkBtn = new Button { Text = Loc.T("btn_clear_ink"), FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(90, 30) };
        clearInkBtn.Pressed += () => _canvas!.ClearInk();
        footer.AddChild(_hoverNode);
        footer.AddChild(_nameEdit);
        footer.AddChild(nameBtn);
        footer.AddChild(drawBtn);
        footer.AddChild(clearInkBtn);
        _combatView.AddChild(footer);

        _currentNode = new Label { Text = "", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _combatView.AddChild(_currentNode);

        // outcome comparison + weights
        var weightsHeader = new Label { Text = "收益权重（越高越重视；战斗结束显示各结局对比）", Modulate = new Color(0.7f, 0.72f, 0.76f), AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _combatView.AddChild(weightsHeader);
        var weightsGrid = new GridContainer { Columns = 2, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        AddWeightSlider(weightsGrid, "战损", 1.0, v => Objective.ObjectiveService.CurrentWeights.HpLost = v);
        AddWeightSlider(weightsGrid, "金币", 0.5, v => Objective.ObjectiveService.CurrentWeights.Gold = v);
        AddWeightSlider(weightsGrid, "药水", 0.5, v => Objective.ObjectiveService.CurrentWeights.Potion = v);
        AddWeightSlider(weightsGrid, "生命上限", 1.0, v => Objective.ObjectiveService.CurrentWeights.MaxHp = v);
        AddWeightSlider(weightsGrid, "遗物计数", 0.5, v => Objective.ObjectiveService.CurrentWeights.Relic = v);
        AddWeightSlider(weightsGrid, "卡牌", 0.5, v => Objective.ObjectiveService.CurrentWeights.Card = v);
        _combatView.AddChild(weightsGrid);

        _outcomeLabel = new Label { Text = "", AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Modulate = new Color(0.85f, 0.9f, 0.88f) };
        _combatView.AddChild(_outcomeLabel);

        // auto search controls
        var searchRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var searchBtn = new Button { Text = "搜索本场战斗", FocusMode = Control.FocusModeEnum.None, TooltipText = "用真实游戏探索本场战斗的状态空间，找最优解（会看到它自己打各种分支）" };
        searchBtn.Pressed += () =>
        {
            if (Search.AutoSearchService.IsSearching)
            {
                return;
            }
            Search.AutoSearchService.StopRequested = false;
            _ = Search.AutoSearchService.RunSearch(2000, fromRoot: true, Search.AutoSearchService.SearchMode.Beam);
        };
        var searchStopBtn = new Button { Text = "停止", FocusMode = Control.FocusModeEnum.None };
        searchStopBtn.Pressed += () => Search.AutoSearchService.StopRequested = true;
        _searchStatus = new Label { Text = "", Modulate = new Color(0.8f, 0.85f, 0.9f), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        searchRow.AddChild(searchBtn);
        searchRow.AddChild(searchStopBtn);
        searchRow.AddChild(_searchStatus);
        _combatView.AddChild(searchRow);
        layout.AddChild(_combatView);

        // run view
        _runView = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill, Visible = false };
        _runView.AddChild(new Label { Text = "运行图：关卡/事件=大结点，战斗可展开小图；点击行=整局跳转。", Modulate = new Color(0.7f, 0.72f, 0.76f), AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        _runScroll = new ScrollContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _runList = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _runScroll.AddChild(_runList);
        _runView.AddChild(_runScroll);
        layout.AddChild(_runView);

        chrome.AddChild(layout);
        _root.AddChild(chrome);
        return _root;
    }

    private static void SetView(string view)
    {
        _view = view;
        _tabCombat!.ButtonPressed = view == "combat";
        _tabRun!.ButtonPressed = view == "run";
        _combatView!.Visible = view == "combat";
        _runView!.Visible = view == "run";
        if (view == "combat")
        {
            _boundGraph = ModEntry.Recorder.Graph;
            _canvas!.SetGraph(ModEntry.Recorder.Graph);
            _canvas.Rebuild();
        }
        _lastVersion = -1;
        _lastRunVersion = -1;
    }

    private static StyleBoxFlat PanelStyle()
    {
        return new StyleBoxFlat
        {
            BgColor = new Color(0.09f, 0.10f, 0.12f, 0.92f),
            BorderColor = new Color(0.24f, 0.26f, 0.32f),
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
        };
    }

    private static void OnNodeClicked(string nodeId)
    {
        _selectedId = nodeId;

        // 战斗图只对应当前战斗：点小结点 = 本场内深快照还原。
        var liveNode = ModEntry.Recorder.Graph.GetNode(nodeId);
        if (liveNode == null)
        {
            return;
        }
        _hoverNode!.Text = liveNode.Name ?? "结点 " + nodeId.Substring(0, 8);
        _nameEdit!.Text = liveNode.Name ?? "";

        bool ok = Restore.RestoreService.RestoreTo(nodeId);
        if (ok)
        {
            ModEntry.Recorder.Graph.SetCurrent(nodeId);
            _currentNode!.Text = $"已还原到结点 {nodeId.Substring(0, 8)}";
        }
        else
        {
            _currentNode!.Text = $"结点 {nodeId.Substring(0, 8)} 无深度快照，无法还原";
        }
        _canvas!.Rebuild();
    }

    private static void OnNodeHovered(string? nodeId)
    {
        if (nodeId == null)
        {
            return;
        }
        var node = ModEntry.Recorder.Graph.GetNode(nodeId);
        if (node == null)
        {
            return;
        }
        var s = node.Snapshot;
        string enemies = string.Join(",", s.Enemies.Select(e => $"{e.Id}:{e.Hp}"));
        _hoverNode!.Text = (node.Name != null ? node.Name + " " : "") +
            $"第{s.TurnNumber}回合 HP {s.PlayerHp}/{s.PlayerMaxHp} 能量{s.Energy} 手牌[{string.Join(",", s.Hand)}] 敌人[{enemies}]";
    }

    private static void ApplyName()
    {
        var graph = ModEntry.Recorder.Graph;
        string? target = _selectedId ?? _canvas?.HoveredNode;
        if (target == null)
        {
            return;
        }
        var node = graph.GetNode(target);
        if (node == null)
        {
            return;
        }
        node.Name = string.IsNullOrWhiteSpace(_nameEdit!.Text) ? null : _nameEdit.Text.Trim();
        _hoverNode!.Text = node.Name ?? "结点 " + target.Substring(0, 8);
        _canvas!.Rebuild();
    }

    public static void OnFrame()
    {
        if (_root == null || !_root.Visible || _rebuilding)
        {
            return;
        }
        if (!_diagLogged)
        {
            _diagLogged = true;
            MegaCrit.Sts2.Core.Logging.Log.Info(
                $"[WorldLineYggdrasil.Ui] panel size={_root.Size} canvas size={_canvas!.Root.Size} graphNodes={ModEntry.Recorder.Graph.Nodes.Count}");
        }
        // Auto-show the run tree when there is no combat to view yet.
        if (_view == "combat" &&
            ModEntry.Recorder.Graph.Nodes.Count == 0 &&
            ModEntry.RunRecorder.Graph.Nodes.Count > 0)
        {
            SetView("run");
        }
        if (_view == "run")
        {
            RefreshRunView();
        }
        else
        {
            RefreshCombatView();
        }
    }
    private static bool _diagLogged;

    private static void AddWeightSlider(GridContainer grid, string label, double initial, Action<double> setter)
    {
        grid.AddChild(new Label { Text = label, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var slider = new HSlider
        {
            MinValue = 0,
            MaxValue = 5,
            Step = 0.5,
            Value = initial,
            CustomMinimumSize = new Vector2(0, 20),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        slider.ValueChanged += v =>
        {
            setter((double)v);
            if (_boundGraph != null)
            {
                Objective.ObjectiveService.RefreshScores(_boundGraph);
                RefreshOutcomeLabel();
            }
        };
        grid.AddChild(slider);
    }

    private static void RefreshOutcomeLabel()
    {
        if (_outcomeLabel == null || _boundGraph == null)
        {
            return;
        }
        var graph = _boundGraph;
        var terminals = graph.Nodes.Where(n => n.IsTerminal && n.Outcome != null).ToList();
        if (terminals.Count == 0)
        {
            _outcomeLabel.Text = "（尚无战斗结局，打完后这里会对比各结局收益）";
            return;
        }
        var best = terminals.OrderByDescending(n => n.Outcome!.Score).First();
        var sb = new System.Text.StringBuilder("结局对比（仅在你已探索的路径内排名；自动搜索上线后会给出真正最优解）：\n");
        foreach (var n in terminals)
        {
            string mark = n == best ? "★最佳 " : "      ";
            sb.AppendLine($"{mark}[{n.TerminalKind}] {n.Outcome!.Summary()}  得分 {n.Outcome.Score:F2}");
        }
        _outcomeLabel.Text = sb.ToString().TrimEnd();
    }

    private static void RefreshCombatView()
    {
        var graph = ModEntry.Recorder.Graph;

        // The live combat graph object is swapped each combat; keep the canvas
        // bound to the current one.
        if (!ReferenceEquals(_boundGraph, graph))
        {
            _boundGraph = graph;
            _canvas!.SetGraph(graph);
            _canvas.Rebuild();
        }

        int version = graph.Nodes.Count * 1000 + graph.Edges.Count;
        if (version == _lastVersion && _counts!.Text != "")
        {
            return;
        }
        _lastVersion = version;
        _counts!.Text = $" {graph.Nodes.Count} 结点 / {graph.Edges.Count} 边";
        RefreshOutcomeLabel();
        if (_searchStatus != null)
        {
            _searchStatus.Text = Search.AutoSearchService.IsSearching
                ? $"搜索中… 边={Search.AutoSearchService.EdgeCount} 终结={Search.AutoSearchService.TerminalCount}"
                : "（未在搜索）";
        }
    }

    private static void RefreshRunView()
    {
        var graph = ModEntry.RunRecorder.Graph;
        if (graph.Seed == "")
        {
            _counts!.Text = " 无进行中的对局";
            return;
        }
        int version = graph.Nodes.Count * 1000 + graph.Edges.Count;
        if (version == _lastRunVersion && _runList!.GetChildCount() > 0)
        {
            return;
        }
        _lastRunVersion = version;
        _counts!.Text = $" 运行图：{graph.Nodes.Count} 大结点 / {graph.Edges.Count} 边";
        RebuildRunList(graph);
    }

    private static void RebuildRunList(RunGraph graph)
    {
        _rebuilding = true;
        try
        {
            foreach (Node child in _runList!.GetChildren())
            {
                child.QueueFree();
            }

            // depth map via BFS from root
            var depth = new Dictionary<string, int>();
            var queue = new Queue<string>();
            if (graph.RootNodeId != null)
            {
                depth[graph.RootNodeId] = 0;
                queue.Enqueue(graph.RootNodeId);
            }
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                foreach (var e in graph.ChildrenOf(cur))
                {
                    if (!depth.ContainsKey(e.ToId))
                    {
                        depth[e.ToId] = depth[cur] + 1;
                        queue.Enqueue(e.ToId);
                    }
                }
            }

            foreach (var n in graph.Nodes)
            {
                int d = depth.TryGetValue(n.Id, out var v) ? v : 0;
                string mark = n.Id == graph.CurrentNodeId ? "▶ " : "";
                string term = n.IsTerminal ? $" [{n.TerminalKind}]" : "";
                string combat = n.CombatScope != null ? $" 小图:{n.CombatScope}" : "";
                string indent = new string(' ', Math.Min(d * 2, 12));
                string label = $"{mark}{indent}{n.Label} (楼层{n.Floor}){term}{combat}";

                var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                var go = new Button { Text = label, Alignment = HorizontalAlignment.Left, ClipText = true, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, TooltipText = label, FocusMode = Control.FocusModeEnum.None, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                string nodeId = n.Id;
                go.Pressed += () => Restore.RunRestoreService.RestoreTo(graph.GetNode(nodeId)!).ContinueWith(_ => { });
                row.AddChild(go);

                if (n.CombatScope != null)
                {
                    // 小图功能已移除：过去战斗的小结点不再保留，只保留整局大结点跳转。
                    // （CombatScope 仍会记录，但不展示小图按钮）
                }
                _runList.AddChild(row);
            }
        }
        finally
        {
            _rebuilding = false;
        }
    }
}