using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Models;

namespace WorldLineYggdrasil.Search;

public enum RealActionKind
{
    PlayCard,
    EndTurn,
    UsePotion,
    DiscardPotion,
}

/// <summary>A legal action in the live game, holding the actual game objects.</summary>
public sealed class RealAction
{
    public RealActionKind Kind { get; init; }
    public CardModel? Card { get; init; }
    public Creature? Target { get; init; }
    public PotionModel? Potion { get; init; }

    public string Label => Kind switch
    {
        RealActionKind.PlayCard => Card?.Id.Entry ?? "?",
        RealActionKind.EndTurn => "end_turn",
        RealActionKind.UsePotion => "potion:" + (Potion?.Id.Entry ?? "?"),
        RealActionKind.DiscardPotion => "discard_potion:" + (Potion?.Id.Entry ?? "?"),
        _ => "?",
    };
}

public static class ActionEnumerator
{
    public static List<RealAction> Enumerate()
    {
        var actions = new List<RealAction>();
        var cs = CombatManager.Instance.DebugOnlyGetState();
        if (cs == null || !cs.IsLiveCombat())
        {
            return actions;
        }
        var player = LocalContext.GetMe(cs);
        if (player?.PlayerCombatState == null)
        {
            return actions;
        }

        foreach (var card in player.PlayerCombatState.Hand.Cards)
        {
            if (!card.CanPlay(out var reason, out _) || reason != UnplayableReason.None)
            {
                continue;
            }
            bool anyTarget = card.TargetType == TargetType.AnyEnemy || card.TargetType == TargetType.AnyAlly;
            if (anyTarget)
            {
                foreach (var creature in cs.Creatures)
                {
                    if (card.IsValidTarget(creature))
                    {
                        actions.Add(new RealAction { Kind = RealActionKind.PlayCard, Card = card, Target = creature });
                    }
                }
            }
            else
            {
                actions.Add(new RealAction { Kind = RealActionKind.PlayCard, Card = card, Target = null });
            }
        }

        if (cs.CurrentSide == CombatSide.Player)
        {
            actions.Add(new RealAction { Kind = RealActionKind.EndTurn });
        }

        foreach (var potion in player.Potions)
        {
            actions.Add(new RealAction { Kind = RealActionKind.DiscardPotion, Potion = potion });
            // self / no-target potions can be used directly on the player
            if (potion.IsValidTarget(player.Creature))
            {
                actions.Add(new RealAction { Kind = RealActionKind.UsePotion, Potion = potion, Target = player.Creature });
            }
        }

        return actions;
    }
}