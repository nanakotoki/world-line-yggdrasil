namespace WorldLineYggdrasil.Simulation;

/// <summary>
/// Static card data used by the simulator. Values verified against the
/// decompiled game for the basic Ironclad set; more cards get added as the
/// simulator's rule coverage grows (M4b).
/// </summary>
public static class CardDatabase
{
    private static readonly Dictionary<string, SimCard> Cards = new();

    static CardDatabase()
    {
        Register(new SimCard { Id = "STRIKE_IRONCLAD", Cost = 1, NeedsTarget = true, Effects = { new SimEffect { Type = SimEffectType.Attack, Amount = 6 } } });
        Register(new SimCard { Id = "DEFEND_IRONCLAD", Cost = 1, Effects = { new SimEffect { Type = SimEffectType.Block, Amount = 5 } } });
        Register(new SimCard { Id = "BASH", Cost = 2, NeedsTarget = true, Effects =
        {
            new SimEffect { Type = SimEffectType.Attack, Amount = 8 },
            new SimEffect { Type = SimEffectType.ApplyPowerToTarget, Amount = 2, PowerId = "VULNERABLE" },
        } });
        Register(new SimCard { Id = "CLOTHESLINE", Cost = 2, NeedsTarget = true, Effects =
        {
            new SimEffect { Type = SimEffectType.Attack, Amount = 12 },
            new SimEffect { Type = SimEffectType.ApplyPowerToTarget, Amount = 2, PowerId = "WEAK" },
        } });
        Register(new SimCard { Id = "CLEAVE", Cost = 1, NeedsTarget = false, Effects = { new SimEffect { Type = SimEffectType.Attack, Amount = 8 } } });
        Register(new SimCard { Id = "IRON_WAVE", Cost = 1, NeedsTarget = true, Effects =
        {
            new SimEffect { Type = SimEffectType.Block, Amount = 5 },
            new SimEffect { Type = SimEffectType.Attack, Amount = 5 },
        } });
        Register(new SimCard { Id = "SLASH_IRONCLAD", Cost = 1, NeedsTarget = true, Effects = { new SimEffect { Type = SimEffectType.Attack, Amount = 5 } } });
        Register(new SimCard { Id = "ANGRY_IRONCLAD", Cost = 1, NeedsTarget = true, Effects = { new SimEffect { Type = SimEffectType.Attack, Amount = 4 } } });
        Register(new SimCard { Id = "FEEL_NO_PAIN", Cost = 1, Effects = { new SimEffect { Type = SimEffectType.ApplyPowerToSelf, Amount = 1, PowerId = "FEEL_NO_PAIN" } } });

        // statuses / generated
        Register(new SimCard { Id = "SLIMED", Cost = 1, IsStatus = true, IsEthereal = true, Effects = { new SimEffect { Type = SimEffectType.AddStatusToDraw, PowerId = "SLIMED" } } });
        Register(new SimCard { Id = "WOUND", Cost = 1, IsStatus = true, IsEthereal = true });
    }

    private static void Register(SimCard c)
    {
        Cards[c.Id] = c;
    }

    public static SimCard? Get(string id)
    {
        return Cards.TryGetValue(id, out var c) ? c : null;
    }
}