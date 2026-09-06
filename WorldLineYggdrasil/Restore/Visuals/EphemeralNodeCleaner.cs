using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using WorldLineYggdrasil.Restore;

namespace WorldLineYggdrasil.Restore.Visuals;

/// <summary>
/// Free every node under NCombatRoom that wasn't present at capture time —
/// catches card-flying-toward-player visuals, damage particles, hit number
/// nodes, and any other VFX the in-flight action spawned.
///
/// We deliberately skip NCard (handled by OrphanCardCleaner using a different
/// rule) so that newly-created hand NCards from HandRefresher aren't freed.
/// </summary>
internal static class EphemeralNodeCleaner
{
    public static void Clean(CombatSnapshot snap)
    {
        // No baseline → can't tell which nodes are pre-existing vs ephemeral.
        // Skip rather than free everything (which would nuke the entire UI).
        if (snap.SceneNodes.Count == 0) return;

        var room = NCombatRoom.Instance;
        if (room == null) return;

        // Collect nodes-to-free first, then free in a second pass — we'd
        // otherwise modify the tree we're walking.
        var toFree = new List<Node>();
        foreach (var n in CombatSnapshot.EnumerateTree(room))
        {
            if (n == null) continue;
            if (snap.SceneNodes.Contains(n)) continue;

            // Skip NCards — managed by HandRefresher + OrphanCardCleaner.
            if (n is MegaCrit.Sts2.Core.Nodes.Cards.NCard) continue;

            // Skip NCreature visuals freshly created by ReviveCreature.
            if (n is MegaCrit.Sts2.Core.Nodes.Combat.NCreature) continue;

            // Skip anything with a structural-sounding name (heuristic: avoids
            // nuking legitimate UI children added by game during normal flow).
            // "CardPlay": NMouseCardPlay / NCardPlayQueue carry input state
            // (NPlayerHand._currentCardPlay refers to the live NMouseCardPlay).
            // Freeing NMouseCardPlay leaves a disposed Godot wrapper in
            // _currentCardPlay; the next click is briefly lost while the
            // game lazily recreates it — observable as "Z 연타 후 카드를
            // 잠시 못 잡음".
            var typeName = n.GetType().Name;
            if (typeName.StartsWith("N") && (
                typeName.Contains("Holder") || typeName.Contains("Pile")
                || typeName.Contains("Container") || typeName.Contains("Display")
                || typeName.Contains("Bar") || typeName.Contains("Button")
                || typeName.Contains("Ui") || typeName.Contains("Panel")
                || typeName.Contains("CardPlay")))
                continue;

            toFree.Add(n);
        }

        foreach (var n in toFree)
        {
            // Kill any tween fields on the node first so callbacks don't fire
            // on a freed instance.
            foreach (var name in new[] { "_tween", "_currentTween", "_positionTween", "_scaleTween" })
            {
                var f = AccessTools.Field(n.GetType(), name);
                if (f?.GetValue(n) is Tween t && t.IsValid())
                {
                    try { t.Kill(); } catch { }
                }
            }
            try { if (n.IsInsideTree()) n.QueueFree(); } catch { }
        }

        if (toFree.Count > 0) RestoreLogger.Info($"[Ephemeral] freed {toFree.Count} node(s)");
    }
}
