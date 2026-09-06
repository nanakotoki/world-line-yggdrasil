using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using WorldLineYggdrasil.Restore;

namespace WorldLineYggdrasil.Restore.Visuals;

/// <summary>
/// Tear down each creature's NPowerContainer and rebuild from the restored powers.
/// Reference impl does the same teardown but leaves stale NCreature subscriptions
/// to old PowerModel.Flashed events — that's the "Invulnerable duplicated" bug.
/// We don't fix that subscription leak yet (would require NCreature internal
/// refactor), but since we preserve PowerModel identity across snapshots, the
/// duplication should not occur for powers that already existed live.
/// </summary>
internal static class PowerRefresher
{
    public static void Refresh(CombatSnapshot snap)
    {
        var room = NCombatRoom.Instance;
        if (room == null) return;

        foreach (var saved in snap.Creatures)
        {
            var creature = saved.Ref;
            if (creature == null) continue;
            var node = room.GetCreatureNode(creature);
            if (node == null) continue;

            // NCreature → NCreatureStateDisplay (search children).
            NCreatureStateDisplay? stateDisplay = null;
            foreach (var child in WalkTree(node))
                if (child is NCreatureStateDisplay sd) { stateDisplay = sd; break; }
            if (stateDisplay == null)
            {
                RestoreLogger.Info($"[Power] no NCreatureStateDisplay for {creature.Name}");
                continue;
            }

            var container = ReflectionCache.NCreatureStateDisplayPowerContainerField?.GetValue(stateDisplay)
                as NPowerContainer;
            if (container == null)
            {
                RestoreLogger.Info($"[Power] no NPowerContainer for {creature.Name}");
                continue;
            }

            // Reattach _creature back-ref on the container. _ExitTree at death
            // detached signal handlers; _creature itself is set up by an outer
            // initializer (NCreatureStateDisplay) that doesn't re-run on revive.
            // Without this, OnPowerApplied/OnPowerRemoved no longer fire and any
            // future power add/remove on the revived creature is invisible.
            try
            {
                var prevCreature = ReflectionCache.NPowerContainerCreatureField?.GetValue(container);
                if (!ReferenceEquals(prevCreature, creature))
                {
                    ReflectionCache.NPowerContainerCreatureField?.SetValue(container, creature);
                    ReflectionCache.NPowerContainerConnectSignalsMethod?.Invoke(container, null);
                    RestoreLogger.Info($"[Power] rebound _creature on container for {creature.Name}");
                }
            }
            catch (Exception ex) { RestoreLogger.Warn($"[Power] _creature rebind failed: {ex.Message}"); }

            var powerNodes = ReflectionCache.NPowerContainerNodesField?.GetValue(container)
                as System.Collections.IList;
            if (powerNodes == null) continue;

            int hadCount = powerNodes.Count;

            // Tear down all NPower visuals — both list entry and tree child.
            // Previously only QueueFree was called; the list-Clear() left no
            // record of what was attached but the QueueFree happens on the
            // NEXT frame, so during this frame the container still has the
            // dead nodes as children. AddChildSafely after a same-name child
            // exists silently no-ops in some Godot binds — the new NPower
            // wouldn't get added. Force-RemoveChild before QueueFree.
            foreach (var p in powerNodes)
            {
                if (p is Node n)
                {
                    try
                    {
                        var parent = n.GetParent();
                        if (parent != null) parent.RemoveChild(n);
                    }
                    catch { }
                    try { n.QueueFree(); } catch { }
                }
            }
            powerNodes.Clear();

            int added = 0, skippedNotVisible = 0, exceptions = 0;
            bool containerInTree = container.IsInsideTree();
            // Re-add via the container's private Add(PowerModel) method.
            foreach (var pm in creature.Powers)
            {
                if (pm == null) continue;
                if (!pm.IsVisible) { skippedNotVisible++; continue; }
                try
                {
                    ReflectionCache.NPowerContainerAddMethod?.Invoke(container, new object[] { pm });
                    added++;
                }
                catch (Exception ex)
                {
                    exceptions++;
                    RestoreLogger.Warn($"[Power] re-add failed for {pm.Id.Entry}: {ex.Message}");
                }
            }

            // Forced visibility patch. NPower scenes default to modulate.a=0
            // and _Ready tween-lerps to 1 over 0.5s; on the freshly-rebuilt
            // container after undo, we want the icon visible immediately
            // rather than 0.5s later.
            //
            // We do NOT re-invoke _Ready here. Godot fires _Ready exactly
            // once when a node enters the tree (whether AddChild was synchronous
            // or deferred), and NPower._Ready connects mouse_entered /
            // mouse_exited signals on that one call. Manually re-invoking
            // _Ready on an in-tree node attempted a second connect of those
            // signals and emitted the "Signal 'mouse_entered' is already
            // connected" Godot ERROR on every undo.
            int patched = 0, alreadyOk = 0;
            for (int i = 0; i < powerNodes.Count; i++)
            {
                if (powerNodes[i] is not CanvasItem ci) continue;
                try
                {
                    var m = ci.Modulate;
                    bool wasInvisible = m.A < 0.99f || !ci.Visible;
                    if (wasInvisible)
                    {
                        ci.Visible = true;
                        ci.Modulate = new Color(m.R, m.G, m.B, 1f);
                        patched++;
                    }
                    else alreadyOk++;
                }
                catch { }
            }

            RestoreLogger.Info($"[Power] {creature.Name}: had={hadCount} model={creature.Powers.Count()} added={added} skipNotVisible={skippedNotVisible} ex={exceptions} containerInTree={containerInTree} patched={patched} alreadyOk={alreadyOk}");
        }
    }

    private static IEnumerable<Node> WalkTree(Node root)
    {
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            foreach (var c in n.GetChildren()) stack.Push(c);
        }
    }
}
