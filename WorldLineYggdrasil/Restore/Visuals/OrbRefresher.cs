using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Orbs;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using WorldLineYggdrasil.Restore;

namespace WorldLineYggdrasil.Restore.Visuals;

/// <summary>
/// Rebuilds the player's NOrbManager child nodes after an undo restores the
/// OrbQueue model. Slot disappearance after undo happens because the in-flight
/// card play frees NOrb children; the model rollback alone restores the data
/// but the visual row stays empty (or worse, missing the empty placeholders so
/// the row collapses entirely).
///
/// Pattern: kill any in-flight tween, free every existing NOrb, then re-add
/// one NOrb per filled slot AND one empty NOrb per remaining capacity slot.
/// Snap final positions immediately so the rebuild reads as a state replacement
/// instead of an animated tween. UpdateVisuals is deferred so sprites are
/// created after the new nodes have entered the scene tree.
/// </summary>
internal static class OrbRefresher
{
    public static void Refresh(CombatSnapshot snap)
    {
        if (!snap.HasOrbData) return;
        if (ReflectionCache.NOrbManagerOrbsField == null
            || ReflectionCache.NOrbManagerContainerField == null) return;

        var room = NCombatRoom.Instance;
        var cm = CombatManager.Instance;
        if (room == null || cm == null) return;
        var cs = ReflectionCache.GetCombatState(cm);
        if (cs == null) return;

        foreach (var ally in cs.Allies)
        {
            var player = ally.Player;
            if (player == null) continue;

            var nCreature = room.GetCreatureNode(ally);
            if (nCreature == null) continue;

            var orbManager = nCreature.OrbManager;
            if (orbManager == null) continue;

            var orbQueue = player.PlayerCombatState?.OrbQueue;
            if (orbQueue == null) continue;

            try { RebuildOrbManager(orbManager, orbQueue); }
            catch (Exception ex) { RestoreLogger.Warn($"[Orbs] visual refresh failed: {ex.Message}"); }
        }
    }

    private static void RebuildOrbManager(NOrbManager orbManager, MegaCrit.Sts2.Core.Entities.Orbs.OrbQueue orbQueue)
    {
        var tween = ReflectionCache.NOrbManagerTweenField?.GetValue(orbManager) as Tween;
        if (tween != null && tween.IsValid()) tween.Kill();

        var nOrbsList = ReflectionCache.NOrbManagerOrbsField!.GetValue(orbManager)
            as System.Collections.IList;
        var container = ReflectionCache.NOrbManagerContainerField!.GetValue(orbManager) as Control;
        if (nOrbsList == null || container == null) return;

        foreach (var n in nOrbsList)
            if (n is Node node) try { node.QueueFree(); } catch { }
        nOrbsList.Clear();

        bool isLocal = orbManager.IsLocal;
        var orbs = orbQueue.Orbs.ToList();
        int capacity = orbQueue.Capacity;

        for (int i = 0; i < orbs.Count; i++)
        {
            var nOrb = NOrb.Create(isLocal, orbs[i]);
            container.AddChild(nOrb);
            nOrbsList.Add(nOrb);
            nOrb.Position = Vector2.Zero;
        }

        // Empty slot placeholders — without these, a 3-slot character undoing
        // back to 0 orbs shows an empty row instead of "[ ][ ][ ]".
        for (int i = orbs.Count; i < capacity; i++)
        {
            var nOrb = NOrb.Create(isLocal);
            container.AddChild(nOrb);
            nOrbsList.Add(nOrb);
            nOrb.Position = Vector2.Zero;
        }

        ReflectionCache.NOrbManagerTweenLayoutMethod?.Invoke(orbManager, null);
        ReflectionCache.NOrbManagerUpdateNavMethod?.Invoke(orbManager, null);

        // Snap to final positions instead of letting TweenLayout animate from
        // (0,0). Arc geometry mirrors the game's own NOrbManager layout math.
        var tweenAfter = ReflectionCache.NOrbManagerTweenField?.GetValue(orbManager) as Tween;
        if (tweenAfter != null && tweenAfter.IsValid()) tweenAfter.Kill();
        if (capacity > 0)
        {
            float arcAngle = 125f;
            float angleStep = capacity > 1 ? arcAngle / (float)(capacity - 1) : 0f;
            float radius = Mathf.Lerp(225f, 300f, ((float)capacity - 3f) / 7f);
            if (!isLocal) radius *= 0.75f;
            float curAngle = arcAngle;
            for (int i = 0; i < nOrbsList.Count && i < capacity; i++)
            {
                float s = Mathf.DegToRad(-25f - curAngle);
                var finalPos = new Vector2(-Mathf.Cos(s), Mathf.Sin(s)) * radius;
                if (nOrbsList[i] is NOrb nOrbItem) nOrbItem.Position = finalPos;
                curAngle -= angleStep;
            }
        }

        // Defer UpdateVisuals so sprite/material setup happens after the new
        // NOrb nodes are in the scene tree (otherwise textures don't bind).
        Callable.From(() =>
        {
            try
            {
                if (ReflectionCache.NOrbManagerOrbsField!.GetValue(orbManager)
                    is not System.Collections.IList list) return;
                foreach (var item in list)
                    if (item is NOrb n) n.UpdateVisuals(false);
            }
            catch (Exception ex) { RestoreLogger.Warn($"[Orbs] deferred UpdateVisuals failed: {ex.Message}"); }
        }).CallDeferred();

        RestoreLogger.Info($"[Orbs] visual rebuilt: {orbs.Count} filled + {capacity - orbs.Count} empty slots");
    }
}
