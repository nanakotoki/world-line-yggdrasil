using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using WorldLineYggdrasil.Restore;
using System.Reflection;

namespace WorldLineYggdrasil.Restore.Visuals;

/// <summary>
/// Aggressively unstuck every state field that gates "can the player play cards
/// or end turn?". After undo, the live state can carry leftovers from the action
/// that was queued (in-flight card play, ending-phase flags, action-disabled flag).
///
/// V0.0.2 bug fix: previously only cleared a subset → cards became unplayable
/// after undo. Now mirrors the reference impl's full reset list.
/// </summary>
internal static class EndTurnStateRefresher
{
    public static void Reset()
    {
        ResetCombatManagerState();
        ResetHandState();
        ResetCardPlayQueue();
    }

    private static void ResetCombatManagerState()
    {
        var cm = CombatManager.Instance;
        if (cm == null) return;

        // Empty the player-ready sets so a fresh "End Turn" click works.
        ClearCollection(ReflectionCache.CmPlayersReadyToEndTurnField?.GetValue(cm));
        ClearCollection(ReflectionCache.CmPlayersReadyToBeginEnemyTurnField?.GetValue(cm));

        // PlayerActionsDisabled = false (via setter so PlayerActionsDisabledChanged fires).
        TrySetBoolProp(cm, ReflectionCache.CmPlayerActionsDisabledProp, false);

        // *** Critical: IsPlayPhase = true. If false, no cards can be played. ***
        TrySetBoolProp(cm, AccessTools.Property(typeof(CombatManager), "IsPlayPhase"), true);

        // Clear "ending player turn" intermediate phase flags — these get set
        // mid-end-turn and stay set if we undo through the transition.
        TrySetBoolProp(cm, AccessTools.Property(typeof(CombatManager), "EndingPlayerTurnPhaseOne"), false);
        TrySetBoolProp(cm, AccessTools.Property(typeof(CombatManager), "EndingPlayerTurnPhaseTwo"), false);
        TrySetBoolProp(cm, AccessTools.Property(typeof(CombatManager), "IsEnemyTurnStarted"), false);
    }

    private static void ResetHandState()
    {
        var hand = NPlayerHand.Instance;
        if (hand == null) return;

        // Use the game's own CancelPlayCard() so internal state machines (which
        // we don't fully understand) get cleaned up via their proper code path.
        // Manual field-reset was leaving subtle desync after Z-spam.
        var currentPlayField = ReflectionCache.HandCurrentCardPlayField;
        var currentPlay = currentPlayField?.GetValue(hand);

        // If the Godot side has been freed (by an older EphemeralNodeCleaner
        // pass, or any future cleanup that catches it) the C# wrapper stays
        // non-null but IsInstanceValid is false. Calling CancelPlayCard on a
        // disposed instance throws or silently no-ops, leaving the field still
        // pointing at the zombie. The next "start a drag" check sees a non-null
        // _currentCardPlay and is briefly ignored. Null it explicitly so the
        // game can lazily recreate NMouseCardPlay on the next click.
        if (currentPlay is GodotObject zombieGo && !GodotObject.IsInstanceValid(zombieGo))
        {
            try { currentPlayField?.SetValue(hand, null); }
            catch (Exception ex) { RestoreLogger.Warn($"[EndTurn] null disposed _currentCardPlay failed: {ex.Message}"); }
            currentPlay = null;
        }

        if (currentPlay != null)
        {
            // Kill any active tween first so CancelPlayCard doesn't wait on it.
            var tweenField = AccessTools.Field(currentPlay.GetType(), "_tween");
            if (tweenField?.GetValue(currentPlay) is Tween tween && tween.IsValid())
                try { tween.Kill(); } catch { }

            // Walk the type hierarchy because CancelPlayCard is on the NCardPlay
            // base class but currentPlay's runtime type is NMouseCardPlay.
            MethodInfo? cancelMethod = null;
            for (var t = currentPlay.GetType(); t != null && cancelMethod == null; t = t.BaseType)
                cancelMethod = AccessTools.Method(t, "CancelPlayCard");
            if (cancelMethod != null)
            {
                try { cancelMethod.Invoke(currentPlay, null); }
                catch (Exception ex) { RestoreLogger.Warn($"[EndTurn] CancelPlayCard failed: {ex.Message}"); }
            }

            // Belt-and-braces: also flip the trying flag in case CancelPlayCard
            // didn't reach that point (e.g. early-returned because no play active).
            var tryingField = AccessTools.Field(currentPlay.GetType(), "_isTryingToPlayCard");
            try { tryingField?.SetValue(currentPlay, false); } catch { }
        }

        // _currentMode = Mode.Play. Mode is an internal nested enum; we look up
        // the value by NAME so we don't depend on its declared int value.
        var modeField = ReflectionCache.HandCurrentModeField;
        if (modeField != null)
        {
            var modeType = modeField.FieldType;
            if (modeType.IsEnum)
            {
                try
                {
                    var playValue = Enum.Parse(modeType, "Play");
                    modeField.SetValue(hand, playValue);
                }
                catch (Exception ex)
                {
                    RestoreLogger.Warn($"[EndTurn] could not set Mode to Play: {ex.Message}");
                }
            }
        }

        // Drag state.
        var draggedField = AccessTools.Field(typeof(NPlayerHand), "_draggedHolderIndex");
        try { draggedField?.SetValue(hand, -1); } catch { }

        // Awaiting queue.
        var awaitField = AccessTools.Field(typeof(NPlayerHand), "_holdersAwaitingQueue");
        ClearCollection(awaitField?.GetValue(hand));

        // _isDisabled = false + restore visual modulate.
        var disabledField = AccessTools.Field(typeof(NPlayerHand), "_isDisabled");
        try
        {
            disabledField?.SetValue(hand, false);
            if (hand is Control control) control.Modulate = Colors.White;
        }
        catch { }
    }

    private static void ResetCardPlayQueue()
    {
        // NCardPlayQueue can hold stale entries (cards mid-tween) that hang the
        // animation flow after undo.
        var pq = NCardPlayQueue.Instance;
        if (pq == null) return;
        var queueField = AccessTools.Field(typeof(NCardPlayQueue), "_playQueue");
        ClearCollection(queueField?.GetValue(pq));
    }

    private static void ClearCollection(object? collection)
    {
        if (collection == null) return;
        var clear = AccessTools.Method(collection.GetType(), "Clear");
        try { clear?.Invoke(collection, null); } catch { }
    }

    private static void TrySetBoolProp(object target, System.Reflection.PropertyInfo? prop, bool value)
    {
        if (prop == null || !prop.CanWrite) return;
        try { prop.SetValue(target, value); }
        catch (Exception ex) { RestoreLogger.Warn($"[EndTurn] {prop.Name} = {value} failed: {ex.Message}"); }
    }
}
