using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using WorldLineYggdrasil.Restore;
using System.Reflection;

namespace WorldLineYggdrasil.Restore.Visuals;

/// <summary>
/// Rebuild NPotionContainer visuals to match player.PotionSlots after a restore.
/// Direct slot writes don't trigger Player.PotionDiscarded / UsedPotionRemoved
/// events, so the visual stays in "potion used" state with the potion icon
/// missing and the holder grayed out.
/// </summary>
internal static class PotionRefresher
{
    private static Type? _containerType;
    private static Type? _holderType;
    private static Type? _potionType;
    private static FieldInfo? _holdersField;
    private static FieldInfo? _holderPotionBackingField;
    private static FieldInfo? _holderDisabledField;
    private static FieldInfo? _holderEmptyIconField;
    private static MethodInfo? _holderAddPotionMethod;
    private static MethodInfo? _potionCreateMethod;
    private static bool _initialized;

    public static void Refresh(CombatSnapshot snap)
    {
        InitTypes();
        if (_containerType == null || _holderType == null || _potionType == null) return;

        var nRun = NRun.Instance;
        if (nRun == null) return;

        var container = FindNodeOfType(nRun, _containerType);
        if (container == null) { RestoreLogger.Warn("[Potion] NPotionContainer not found in scene"); return; }

        var holders = _holdersField?.GetValue(container) as System.Collections.IList;
        if (holders == null) return;

        // We need the live Player to read its PotionSlots (post-restore).
        var player = ResolvePlayer();
        if (player == null) return;

        int n = Math.Min(holders.Count, player.PotionSlots.Count);
        int rebuilt = 0;
        for (int i = 0; i < n; i++)
        {
            var holder = holders[i] as Node;
            if (holder == null) continue;
            var desired = player.PotionSlots[i];

            try
            {
                // Remove existing NPotion children + reset holder state.
                foreach (var child in holder.GetChildren())
                {
                    if (_potionType.IsInstanceOfType(child))
                    {
                        holder.RemoveChild(child);
                        ((Node)child).QueueFree();
                    }
                }
                _holderPotionBackingField?.SetValue(holder, null);
                _holderDisabledField?.SetValue(holder, false);
                if (holder is Control hControl) hControl.Modulate = Colors.White;
                if (_holderEmptyIconField?.GetValue(holder) is Control emptyIcon)
                    emptyIcon.Modulate = Colors.White;

                // Add the desired potion if any.
                if (desired != null && _potionCreateMethod != null && _holderAddPotionMethod != null)
                {
                    var nPotion = _potionCreateMethod.Invoke(null, new object[] { desired });
                    if (nPotion != null)
                    {
                        ((Node)nPotion).Set("position", new Vector2(-30f, -30f));
                        _holderAddPotionMethod.Invoke(holder, new[] { nPotion });
                        rebuilt++;
                    }
                }
            }
            catch (Exception ex) { RestoreLogger.Warn($"[Potion] slot[{i}]: {ex.Message}"); }
        }

        RestoreLogger.Info($"[Potion] visuals rebuilt: {rebuilt} potion(s) reattached");
    }

    private static void InitTypes()
    {
        if (_initialized) return;
        _initialized = true;

        _containerType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Potions.NPotionContainer");
        _holderType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Potions.NPotionHolder");
        _potionType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Potions.NPotion");

        if (_containerType != null)
            _holdersField = AccessTools.Field(_containerType, "_holders");

        if (_holderType != null)
        {
            _holderPotionBackingField = AccessTools.Field(_holderType, "<Potion>k__BackingField");
            _holderDisabledField = AccessTools.Field(_holderType, "_disabledUntilPotionRemoved");
            _holderEmptyIconField = AccessTools.Field(_holderType, "_emptyIcon");
            _holderAddPotionMethod = AccessTools.Method(_holderType, "AddPotion");
        }

        if (_potionType != null)
            _potionCreateMethod = AccessTools.Method(_potionType, "Create", new[] { typeof(PotionModel) });

        RestoreLogger.Info($"[Potion] reflection: container={_containerType != null} holder={_holderType != null} " +
            $"potion={_potionType != null} create={_potionCreateMethod != null} add={_holderAddPotionMethod != null}");
    }

    private static Player? ResolvePlayer()
    {
        var rm = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
        var stateProp = AccessTools.Property(typeof(MegaCrit.Sts2.Core.Runs.RunManager), "State");
        var runState = stateProp?.GetValue(rm) as MegaCrit.Sts2.Core.Runs.RunState;
        return runState?.Players.FirstOrDefault();
    }

    private static Node? FindNodeOfType(Node root, Type targetType)
    {
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (targetType.IsInstanceOfType(n)) return n;
            foreach (var c in n.GetChildren()) stack.Push(c);
        }
        return null;
    }
}
