using HarmonyLib;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;

namespace WorldLineYggdrasil.Search;

/// <summary>
/// Speeds up the real game while auto-search is running: switches the vanilla
/// FastMode to Instant (skips all scaled waits) and scales Spine animations.
/// Both are gated by <see cref="Active"/> so normal play is unaffected.
/// </summary>
public static class SearchSpeed
{
    public static bool Active;
    /// <summary>Suppress card fly VFX (deal / draw / shuffle animations) while a
    /// semantic cross-combat restore re-enters a combat. Set by the restore path
    /// and cleared after the state settles. (Deliberately NOT the game's TestMode,
    /// which gates far too much real gameplay logic.)</summary>
    public static bool SuppressCardTweens;

    private static FastModeType _savedFastMode = FastModeType.Normal;

    public static void Begin()
    {
        if (Active)
        {
            return;
        }
        Active = true;
        try
        {
            if (MegaCrit.Sts2.Core.Saves.SaveManager.Instance?.PrefsSave != null)
            {
                _savedFastMode = SaveManager.Instance.PrefsSave.FastMode;
                SaveManager.Instance.PrefsSave.FastMode = FastModeType.Instant;
            }
        }
        catch
        {
            // non-fatal
        }
    }

    public static void End()
    {
        if (!Active)
        {
            return;
        }
        Active = false;
        try
        {
            if (MegaCrit.Sts2.Core.Saves.SaveManager.Instance?.PrefsSave != null)
            {
                SaveManager.Instance.PrefsSave.FastMode = _savedFastMode;
            }
        }
        catch
        {
        }
    }
}

/// <summary>Speed up Spine animations while search is active.</summary>
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Bindings.MegaSpine.MegaAnimationState), nameof(MegaCrit.Sts2.Core.Bindings.MegaSpine.MegaAnimationState.SetTimeScale))]
public static class AnimationSpeedPatch
{
    public const float Multiplier = 6f;

    [HarmonyPrefix]
    private static void Prefix(ref float scale)
    {
        if (!SearchSpeed.Active)
        {
            return;
        }
        scale *= Multiplier;
    }
}

/// <summary>Suppress the card-fly VFX (deal / draw / shuffle) while a cross-combat
/// restore is re-entering a combat, so the opening deal doesn't animate on each
/// step jump. These Create methods return a nullable node; a prefix returning null
/// short-circuits the flying-card animation without touching game logic.</summary>
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.Vfx.NCardFlyVfx), "Create",
    new[] { typeof(MegaCrit.Sts2.Core.Nodes.Cards.NCard), typeof(MegaCrit.Sts2.Core.Entities.Cards.PileType), typeof(bool), typeof(string) })]
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.Vfx.NCardFlyVfx), "Create",
    new[] { typeof(MegaCrit.Sts2.Core.Nodes.Cards.NCard), typeof(MegaCrit.Sts2.Core.Entities.Creatures.Creature), typeof(string) })]
public static class CardFlyVfxSuppressPatch
{
    [HarmonyPrefix]
    private static bool Prefix(ref MegaCrit.Sts2.Core.Nodes.Vfx.NCardFlyVfx? __result)
    {
        if (!SearchSpeed.SuppressCardTweens)
        {
            return true;
        }
        __result = null;
        return false;
    }
}

[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.Vfx.NCardFlyShuffleVfx), "Create")]
public static class CardFlyShuffleVfxSuppressPatch
{
    [HarmonyPrefix]
    private static bool Prefix(ref MegaCrit.Sts2.Core.Nodes.Vfx.NCardFlyShuffleVfx? __result)
    {
        if (!SearchSpeed.SuppressCardTweens)
        {
            return true;
        }
        __result = null;
        return false;
    }
}