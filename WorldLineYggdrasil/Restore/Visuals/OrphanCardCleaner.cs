using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace WorldLineYggdrasil.Restore.Visuals;

/// <summary>
/// Walks the scene tree under NGame and frees every NCard that ISN'T currently
/// the registered CardNode of an active hand holder.
///
/// Why: a card mid-lift/mid-return is reparented out of its NHandCardHolder
/// (typically into NCardPlay or onto an overlay layer). After undo, HandRefresher
/// rebuilds hand visuals by checking each holder's CardNode — the lifted card's
/// holder shows null, so a NEW NCard gets created. The old in-flight NCard
/// remains stuck on screen with a tween still running.
///
/// Approach: collect the set of "legitimate" NCards (= the CardNode of every
/// active holder), walk the entire scene tree, free every other NCard. Runs
/// BEFORE HandRefresher so its add-path operates on a clean slate.
/// </summary>
internal static class OrphanCardCleaner
{
    public static void Clean()
    {
        var root = NGame.Instance;
        if (root == null) return;

        var hand = NPlayerHand.Instance;
        var legitimate = new HashSet<NCard>(ReferenceEqualityComparer.Instance);
        if (hand != null)
        {
            foreach (var holder in hand.ActiveHolders)
            {
                if (holder.CardNode is NCard nc) legitimate.Add(nc);
            }
        }

        int freed = 0, skipped = 0, skippedScreen = 0;
        foreach (var node in EnumerateAll(root))
        {
            if (node is not NCard card) continue;
            if (legitimate.Contains(card)) { skipped++; continue; }

            // Skip NCards owned by a screen overlay (NInspectCardScreen,
            // NChooseACardSelectionScreen, etc.). These are NOT hand-play
            // orphans — they're scene-scoped children of the screen, and
            // freeing them leaves the screen's `_card` / `_cardRow` field
            // pointing at a disposed Godot wrapper. The next tween or
            // close-callback on the screen (e.g. NInspectCardScreen.Close →
            // TweenProperty(_card, "modulate", ...)) then throws
            // ObjectDisposedException.
            if (HasScreenAncestor(card)) { skippedScreen++; continue; }

            // Kill any running tween before queue-free so cleanup can't be
            // resurrected by a tween callback.
            KillKnownTweens(card);

            try
            {
                if (card.IsInsideTree()) card.QueueFree();
                freed++;
            }
            catch (Exception ex) { RestoreLogger.Warn($"[Orphan] free failed: {ex.Message}"); }
        }

        if (freed > 0 || skippedScreen > 0)
            RestoreLogger.Info($"[Orphan] freed={freed} kept={skipped} keptScreen={skippedScreen}");
    }

    /// <summary>
    /// Walk up the parent chain; return true if any ancestor's type name
    /// ends with "Screen" or its namespace starts with the screens root.
    /// Substring-match keeps us decoupled from concrete class references
    /// (NInspectCardScreen, NChooseACardSelectionScreen, NInspectRelicScreen,
    /// NEpochInspectScreen, …) — adding a new screen subclass in a future
    /// STS2 update doesn't need a code update here.
    /// </summary>
    private static bool HasScreenAncestor(Node node)
    {
        var p = node.GetParent();
        while (p != null)
        {
            var t = p.GetType();
            if (t.Name.EndsWith("Screen", StringComparison.Ordinal)) return true;
            var ns = t.Namespace;
            if (ns != null && ns.Contains(".Screens", StringComparison.Ordinal)) return true;
            p = p.GetParent();
        }
        return false;
    }

    private static void KillKnownTweens(NCard card)
    {
        // NCard internals tend to hold tweens under names like "_tween",
        // "_currentTween", "_positionTween". Try a few common ones.
        foreach (var name in new[] { "_tween", "_currentTween", "_positionTween", "_scaleTween" })
        {
            var f = AccessTools.Field(card.GetType(), name);
            if (f?.GetValue(card) is Tween t && t.IsValid())
            {
                try { t.Kill(); } catch { }
            }
        }
    }

    private static IEnumerable<Node> EnumerateAll(Node root)
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
