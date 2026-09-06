using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes;

namespace WorldLineYggdrasil.Ui;

/// <summary>
/// Installs the World Line Yggdrasil overlay into the game's scene tree:
/// an edge tab button ("图") toggles a panel containing the combat graph
/// canvas (nodes = states, edges = actions, click a node to restore it).
/// </summary>
public static class WlyOverlayHost
{
    private const string LogPrefix = "[WorldLineYggdrasil.Ui]";

    private static CanvasLayer? _layer;
    private static Control? _panel;
    private static Button? _edgeTab;
    private static SceneTree? _tree;
    private static bool _visible;
    private static int _buildAttempts;

    public static void Install()
    {
        TryBuildOrRetry();
    }

    public static void Uninstall()
    {
        try
        {
            if (_layer != null && _layer.IsInsideTree())
            {
                _layer.QueueFree();
            }
        }
        catch
        {
        }
        _layer = null;
        _panel = null;
        _edgeTab = null;
    }

    private static void TryBuildOrRetry()
    {
        if (_layer != null)
        {
            return;
        }
        if (NGame.Instance != null)
        {
            Build();
            return;
        }
        if (_buildAttempts++ > 180)
        {
            Log.Warn($"{LogPrefix} NGame never became ready; overlay unavailable this session");
            return;
        }
        Callable.From(TryBuildOrRetry).CallDeferred();
    }

    private static void Build()
    {
        var game = NGame.Instance;
        if (game == null)
        {
            TryBuildOrRetry();
            return;
        }

        _tree = game.GetTree();
        var root = _tree.Root;

        _layer = new CanvasLayer
        {
            Name = "WorldLineYggdrasilOverlay",
            Layer = 128,
            ProcessMode = Node.ProcessModeEnum.Always,
        };

        var host = new Control
        {
            Name = "WlyHost",
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        host.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        _edgeTab = new Button
        {
            Name = "WlyEdgeTab",
            Text = "图",
            CustomMinimumSize = new Vector2(40, 72),
            TooltipText = "世界线·战斗状态图 (World Line Yggdrasil)",
            FocusMode = Control.FocusModeEnum.None,
        };
        // Positioned on the right edge, clearly BELOW the game's top bar /
        // deck button so players don't misclick. Distinct dark style.
        _edgeTab.AnchorLeft = 1;
        _edgeTab.AnchorRight = 1;
        _edgeTab.AnchorTop = 0;
        _edgeTab.AnchorBottom = 0;
        _edgeTab.OffsetLeft = -128;
        _edgeTab.OffsetRight = -88;
        _edgeTab.OffsetTop = 170;
        _edgeTab.OffsetBottom = 242;
        _edgeTab.AddThemeStyleboxOverride("normal", TabStyle(new Color(0.10f, 0.11f, 0.14f, 0.96f)));
        _edgeTab.AddThemeStyleboxOverride("hover", TabStyle(new Color(0.16f, 0.18f, 0.23f, 0.98f)));
        _edgeTab.AddThemeStyleboxOverride("pressed", TabStyle(new Color(0.07f, 0.08f, 0.10f, 1f)));
        _edgeTab.AddThemeFontSizeOverride("font_size", 18);
        _edgeTab.Pressed += ToggleVisible;

        _panel = GraphPanel.Build();
        _panel.Visible = true;   // open by default so the user sees the graph + diagnostics fire

        host.AddChild(_edgeTab);
        host.AddChild(_panel);
        _layer.AddChild(host);
        root.CallDeferred(Node.MethodName.AddChild, _layer);

        _tree.ProcessFrame += OnProcessFrame;
        Log.Info($"{LogPrefix} overlay installed");
    }

    private static StyleBoxFlat TabStyle(Color bg)
    {
        var sb = new StyleBoxFlat
        {
            BgColor = bg,
            BorderColor = new Color(0.42f, 0.55f, 0.85f),
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 6,
            CornerRadiusBottomLeft = 6,
        };
        return sb;
    }

    private static void OnProcessFrame()
    {
        GraphPanel.OnFrame();
    }

    private static void ToggleVisible()
    {
        _visible = !_visible;
        if (_panel != null)
        {
            _panel.Visible = _visible;
        }
    }
}