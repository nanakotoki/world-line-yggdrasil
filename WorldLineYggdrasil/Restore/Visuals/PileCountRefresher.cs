using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using WorldLineYggdrasil.Restore;
using System.Reflection;

namespace WorldLineYggdrasil.Restore.Visuals;

/// <summary>
/// NCombatCardPile (draw / discard / exhaust pile count buttons) tracks count via
/// CardPile.CardAddFinished / CardRemoveFinished events. Bulk-replacing _cards
/// bypasses those events, so the displayed count goes stale.
///
/// Direct fix: set `_currentCount` to actual `pile.Cards.Count` and rewrite the
/// label text. Use `_pile` field (not the public `Pile` property) — reference
/// impl uses the field; the property may not be exposed on every game version.
/// </summary>
internal static class PileCountRefresher
{
    private static FieldInfo? _pileFieldCache;

    public static void Refresh(CombatSnapshot snap)
    {
        RefreshLive();
    }

    /// <summary>Refreshes draw/discard/exhaust pile count labels from the LIVE piles
    /// (used by the semantic restore, which has no deep snapshot).</summary>
    public static void RefreshLive()
    {
        if (ReflectionCache.NCombatCardPileType == null)
        {
            RestoreLogger.Warn("[PileCount] NCombatCardPileType null — cannot refresh");
            return;
        }
        var room = NCombatRoom.Instance;
        if (room == null) return;

        if (_pileFieldCache == null)
            _pileFieldCache = AccessTools.Field(ReflectionCache.NCombatCardPileType, "_pile");

        int updated = 0, skipped = 0;
        foreach (var node in EnumerateOfType(room, ReflectionCache.NCombatCardPileType))
        {
            // Read pile via _pile field (reference impl pattern).
            if (_pileFieldCache?.GetValue(node) is not CardPile pile)
            {
                skipped++;
                continue;
            }

            int actual = pile.Cards.Count;
            int beforeCount = -1;
            try { beforeCount = (int)(ReflectionCache.NCombatCardPileCurrentCountField?.GetValue(node) ?? -1); }
            catch { }

            // 1. Set the backing int.
            ReflectionCache.NCombatCardPileCurrentCountField?.SetValue(node, actual);

            // 2. Force the label text.
            var label = ReflectionCache.NCombatCardPileCountLabelField?.GetValue(node);
            if (label != null)
            {
                var setText = AccessTools.Method(label.GetType(), "SetTextAutoSize");
                if (setText != null)
                {
                    try { setText.Invoke(label, new object[] { actual.ToString() }); }
                    catch (Exception ex) { RestoreLogger.Warn($"[PileCount] SetTextAutoSize failed: {ex.Message}"); }
                }
                else if (label is Label gd) gd.Text = actual.ToString();
            }

            RestoreLogger.Info($"[PileCount] {pile.Type} display {beforeCount}→{actual}");
            updated++;
        }

        RestoreLogger.Info($"[PileCount] updated={updated} skipped={skipped}");
    }

    private static IEnumerable<Node> EnumerateOfType(Node root, System.Type targetType)
    {
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (targetType.IsInstanceOfType(n)) yield return n;
            foreach (var c in n.GetChildren()) stack.Push(c);
        }
    }
}
