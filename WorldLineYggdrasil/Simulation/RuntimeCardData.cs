using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

namespace WorldLineYggdrasil.Simulation;

/// <summary>
/// Builds <see cref="SimCard"/> definitions directly from the REAL card objects
/// at runtime (cost, target, damage/block/draw/energy, common powers). This lets
/// the simulator handle arbitrary cards without a hand-written database.
/// Exotic effects (conditionals, random, orbs, ...) are not captured and return
/// null so the search reports them as "not simulated".
/// </summary>
public static class RuntimeCardData
{
    /// <summary>Cards whose play opens a selection prompt (choose from piles);
    /// not modeled, so they are excluded from the simulator rather than proposed
    /// in plans the real game cannot replay without a UI choice.</summary>
    private static readonly HashSet<string> ChoiceCards = new() { "NEOWS_FURY", "PURITY", "HEADBUTT" };

    public static SimCard? Build(CardModel card)
    {
        try
        {
            if (card.EnergyCost.CostsX)
            {
                return null; // X-cost not modeled yet
            }
            if (ChoiceCards.Contains(card.Id.Entry))
            {
                return null; // choice card, not simulatable yet
            }
            var dv = card.DynamicVars;
            var sim = new SimCard
            {
                Id = card.Id.Entry,
                Cost = card.EnergyCost.GetWithModifiers(CostModifiers.None),
                NeedsTarget = card.TargetType == TargetType.AnyEnemy || card.TargetType == TargetType.AnyAlly,
                IsExhaust = card.CanonicalKeywords.Contains(CardKeyword.Exhaust),
                BlockEqualsDamageDealt = card.Id.Entry == "FISTICUFFS",
            };

            AddAttack(sim, dv, "Damage");
            AddBlock(sim, dv, "Block");
            AddDraw(sim, dv, "Cards");
            AddEnergy(sim, dv, "Energy");
            AddHpLoss(sim, dv, "HpLoss");
            AddPowerToSelf(sim, dv, "STRENGTH", "Strength", "StrengthPower");
            AddPowerToTarget(sim, dv, "VULNERABLE", "Vulnerable", "VulnerablePower");
            AddPowerToTarget(sim, dv, "WEAK", "Weak", "WeakPower");
            AddPowerToTarget(sim, dv, "POISON", "Poison", "PoisonPower");

            if (sim.Effects.Count == 0)
            {
                return null; // no modeled effect -> exotic card, skip
            }
            return sim;
        }
        catch (Exception ex)
        {
            Log.Info($"[WorldLineYggdrasil.Cards] Build failed for {card.Id.Entry}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static bool Has(DynamicVarSet dv, string key) => dv.ContainsKey(key);

    /// <summary>Reads a dynamic var by any of several candidate names
    /// (PowerVar&lt;T&gt; uses the power type name, e.g. "VulnerablePower").</summary>
    private static decimal Var(DynamicVarSet dv, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (dv.TryGetValue(k, out var v))
            {
                return v.BaseValue;
            }
        }
        return 0m;
    }

    private static void AddAttack(SimCard sim, DynamicVarSet dv, string key)
    {
        if (Var(dv, key) > 0)
        {
            sim.Effects.Add(new SimEffect { Type = SimEffectType.Attack, Amount = (int)Var(dv, key) });
        }
    }

    private static void AddBlock(SimCard sim, DynamicVarSet dv, string key)
    {
        if (Var(dv, key) > 0)
        {
            sim.Effects.Add(new SimEffect { Type = SimEffectType.Block, Amount = (int)Var(dv, key) });
        }
    }

    private static void AddDraw(SimCard sim, DynamicVarSet dv, string key)
    {
        if (Var(dv, key) > 0)
        {
            sim.Effects.Add(new SimEffect { Type = SimEffectType.Draw, Amount = (int)Var(dv, key) });
        }
    }

    private static void AddEnergy(SimCard sim, DynamicVarSet dv, string key)
    {
        if (Var(dv, key) > 0)
        {
            sim.Effects.Add(new SimEffect { Type = SimEffectType.GainEnergy, Amount = (int)Var(dv, key) });
        }
    }

    private static void AddHpLoss(SimCard sim, DynamicVarSet dv, string key)
    {
        if (Var(dv, key) > 0)
        {
            sim.Effects.Add(new SimEffect { Type = SimEffectType.LoseHp, Amount = (int)Var(dv, key) });
        }
    }

    private static void AddPowerToSelf(SimCard sim, DynamicVarSet dv, string powerId, params string[] keys)
    {
        if (Var(dv, keys) > 0)
        {
            sim.Effects.Add(new SimEffect { Type = SimEffectType.ApplyPowerToSelf, Amount = (int)Var(dv, keys), PowerId = powerId });
        }
    }

    private static void AddPowerToTarget(SimCard sim, DynamicVarSet dv, string powerId, params string[] keys)
    {
        if (Var(dv, keys) > 0)
        {
            sim.Effects.Add(new SimEffect { Type = SimEffectType.ApplyPowerToTarget, Amount = (int)Var(dv, keys), PowerId = powerId });
        }
    }

    /// <summary>
    /// Builds a card resolver from the LIVE combat's actual cards (covers every
    /// card present in this combat automatically), falling back to the static
    /// database. Call while the real combat is running.
    /// </summary>
    public static Func<string, SimCard?> BuildCombatCardResolver()
    {
        var db = new Dictionary<string, SimCard>();
        try
        {
            var cs = CombatManager.Instance.DebugOnlyGetState();
            var player = cs?.Players.FirstOrDefault();
            if (player?.PlayerCombatState != null)
            {
                foreach (var card in player.PlayerCombatState.AllCards)
                {
                    var sim = Build(card);
                    if (sim != null)
                    {
                        db[card.Id.Entry] = sim;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Info($"[WorldLineYggdrasil.Cards] resolver build failed: {ex.Message}");
        }
        return id => db.TryGetValue(id, out var sim) ? sim : CardDatabase.Get(id);
    }

    /// <summary>
    /// Captures the live game's "Shuffle" RNG state so the simulator reproduces
    /// reshuffles with the exact same card order as the real game.
    /// </summary>
    public static MegaRandom? CaptureShuffleRng()
    {
        try
        {
            var run = RunManager.Instance.DebugOnlyGetState();
            if (run?.Rng?.Shuffle != null)
            {
                return new MegaRandom(run.Rng.Shuffle.ToSerializable());
            }
        }
        catch
        {
        }
        return null;
    }

    public static MegaRandom? CaptureMonsterRng()
    {
        try
        {
            var run = RunManager.Instance.DebugOnlyGetState();
            if (run?.Rng?.MonsterAi != null)
            {
                return new MegaRandom(run.Rng.MonsterAi.ToSerializable());
            }
        }
        catch
        {
        }
        return null;
    }
}