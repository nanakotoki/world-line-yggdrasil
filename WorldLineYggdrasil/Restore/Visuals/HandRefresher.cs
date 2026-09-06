using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using WorldLineYggdrasil.Restore;

namespace WorldLineYggdrasil.Restore.Visuals;

/// <summary>
/// Full-rebuild approach matching the reference impl: remove ALL current visual
/// cards, then recreate from the restored hand pile in order.
///
/// Why not diff-based: holder identity drifts under repeated undo, leading to
/// "card visible but unselectable" — input routes to a holder that doesn't match
/// the visual the user clicks on. Full rebuild eliminates the holder-drift class
/// of bug at the cost of recreating NCards each undo (cheap; ~5 cards typical).
/// </summary>
internal static class HandRefresher
{
    public static void Refresh(CombatSnapshot snap)
    {
        var hand = NPlayerHand.Instance;
        if (hand == null) return;
        if (!snap.PileRefs.TryGetValue(PileType.Hand, out var savedHand)) return;
        Rebuild(hand, savedHand);
    }

    /// <summary>
    /// Rebuilds the on-screen hand to match a list of live CardModels (used by the
    /// semantic restore, which has no deep snapshot — the models come from the
    /// freshly rebuilt live hand pile). Without this the model hand updates but the
    /// rendered cards stay frozen at the opening deal.
    /// </summary>
    public static void RefreshFromLive(IReadOnlyList<CardModel> liveHandCards)
    {
        var hand = NPlayerHand.Instance;
        if (hand == null) return;
        Rebuild(hand, liveHandCards);
    }

    private static void Rebuild(NPlayerHand hand, IReadOnlyList<CardModel> target)
    {
        int holdersBefore = hand.ActiveHolders.Count;

        // Step 1 — record the current cards (those whose holder still has a
        // CardNode). Phantoms from interrupted plays show up as holders with
        // null CardNode and don't carry a CardModel we could pass to Remove.
        var currentCards = new List<CardModel>();
        foreach (var holder in hand.ActiveHolders)
        {
            var nc = holder.CardNode;
            if (nc?.Model is CardModel cm) currentCards.Add(cm);
        }

        // Step 2 — Remove cards we know about via the public API (lets game do
        // its proper unsubscribe / pile-binding cleanup).
        foreach (var card in currentCards)
        {
            try { hand.Remove(card); }
            catch (Exception ex) { RestoreLogger.Warn($"[Hand] remove {card.Id} failed: {ex.Message}"); }
        }

        // Step 3 — Force-remove phantom holders left over (CardNode == null).
        // Their existence makes ActiveHolders.Count > expected card count which
        // closes NPlayerHand.CanPlayCards even though all our flag-resets pass.
        ForceRemovePhantomHolders(hand);

        // Step 4 — Recreate in restored order.
        for (int i = 0; i < target.Count; i++)
        {
            try
            {
                var nc = NCard.Create(target[i], ModelVisibility.Visible);
                nc.Scale = Vector2.One;
                hand.Add(nc, i);
            }
            catch (Exception ex) { RestoreLogger.Warn($"[Hand] add {target[i].Id} at {i} failed: {ex.Message}"); }
        }

        try { hand.ForceRefreshCardIndices(); } catch { }

        int holdersAfter = hand.ActiveHolders.Count;
        RestoreLogger.Info($"[Hand] rebuilt: cards {currentCards.Count}→{target.Count} | holders {holdersBefore}→{holdersAfter}");
    }

    private static void ForceRemovePhantomHolders(NPlayerHand hand)
    {
        // ActiveHolders is a property that probably wraps a private list. To
        // force-eject a phantom we need that list directly. Try common field
        // names; bail silently if none match this game version.
        var holdersField = HarmonyLib.AccessTools.Field(typeof(NPlayerHand), "_holders")
            ?? HarmonyLib.AccessTools.Field(typeof(NPlayerHand), "_activeHolders")
            ?? HarmonyLib.AccessTools.Field(typeof(NPlayerHand), "<ActiveHolders>k__BackingField");
        if (holdersField?.GetValue(hand) is not System.Collections.IList list) return;

        int removed = 0;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            // Each item is an NHandCardHolder; its CardNode is null when the
            // card has been removed but the holder lingers.
            if (list[i] is not Node holderNode) continue;
            var cardNodeProp = HarmonyLib.AccessTools.Property(holderNode.GetType(), "CardNode");
            var card = cardNodeProp?.GetValue(holderNode);
            if (card != null) continue;

            list.RemoveAt(i);
            try { holderNode.QueueFree(); } catch { }
            removed++;
        }
        if (removed > 0) RestoreLogger.Info($"[Hand] force-removed {removed} phantom holder(s)");
    }
}
