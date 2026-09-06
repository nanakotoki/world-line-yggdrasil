using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Runs;

namespace WorldLineYggdrasil.Restore.Visuals;

/// <summary>
/// Dumps every state field that gates "can the player click cards in hand?".
/// Run after restore so a probe.log shows exactly which gate is closed when
/// the user reports cards-not-selectable.
/// </summary>
internal static class InputStateDiagnostics
{
    public static void Dump()
    {
        try
        {
            var cm = CombatManager.Instance;
            var hand = NPlayerHand.Instance;
            var rm = RunManager.Instance;

            string cmInfo = "null";
            if (cm != null)
            {
                var actionsDisabled = AccessTools.Property(typeof(CombatManager), "PlayerActionsDisabled")?.GetValue(cm);
                var isPlayPhase = AccessTools.Property(typeof(CombatManager), "IsPlayPhase")?.GetValue(cm);
                var endingP1 = AccessTools.Property(typeof(CombatManager), "EndingPlayerTurnPhaseOne")?.GetValue(cm);
                var endingP2 = AccessTools.Property(typeof(CombatManager), "EndingPlayerTurnPhaseTwo")?.GetValue(cm);
                var enemyTS = AccessTools.Property(typeof(CombatManager), "IsEnemyTurnStarted")?.GetValue(cm);
                var inProgress = AccessTools.Property(typeof(CombatManager), "IsInProgress")?.GetValue(cm);
                var paused = AccessTools.Property(typeof(CombatManager), "IsPaused")?.GetValue(cm);
                cmInfo = $"PlayerActionsDisabled={actionsDisabled} IsPlayPhase={isPlayPhase} " +
                         $"EndingP1={endingP1} EndingP2={endingP2} EnemyTurnStarted={enemyTS} " +
                         $"InProgress={inProgress} Paused={paused}";
            }
            RestoreLogger.Info($"[Diag] CombatManager: {cmInfo}");

            string handInfo = "null";
            if (hand != null)
            {
                var isDisabled = AccessTools.Field(typeof(NPlayerHand), "_isDisabled")?.GetValue(hand);
                var mode = AccessTools.Field(typeof(NPlayerHand), "_currentMode")?.GetValue(hand);
                var currentPlay = AccessTools.Field(typeof(NPlayerHand), "_currentCardPlay")?.GetValue(hand);
                var dragged = AccessTools.Field(typeof(NPlayerHand), "_draggedHolderIndex")?.GetValue(hand);
                bool? canPlay = null;
                try
                {
                    var m = AccessTools.Method(typeof(NPlayerHand), "CanPlayCards");
                    if (m != null) canPlay = m.Invoke(hand, null) as bool?;
                }
                catch { }
                Vector2? modulate = null;
                if (hand is Control c) modulate = new Vector2(c.Modulate.A, c.Visible ? 1 : 0);
                string playInfo = "null";
                if (currentPlay != null)
                {
                    // EphemeralNodeCleaner QueueFree's the NMouseCardPlay during
                    // restore; by the time this dump runs, the C# wrapper still
                    // points at a disposed Godot object. Skip when invalid.
                    if (currentPlay is GodotObject go && !GodotObject.IsInstanceValid(go))
                    {
                        playInfo = "<disposed>";
                    }
                    else
                    {
                        var t = currentPlay.GetType();
                        var trying = AccessTools.Field(t, "_isTryingToPlayCard")?.GetValue(currentPlay);
                        var cardField = AccessTools.Field(t, "_card")?.GetValue(currentPlay);
                        var children = (currentPlay as Node)?.GetChildCount() ?? -1;
                        playInfo = $"<{t.Name} _isTryingToPlayCard={trying} _card={(cardField != null ? "set" : "null")} children={children}>";
                    }
                }
                handInfo = $"_isDisabled={isDisabled} mode={mode} currentCardPlay={playInfo} " +
                           $"draggedHolderIndex={dragged} CanPlayCards={canPlay} " +
                           $"holders={hand.ActiveHolders.Count} (alpha,visible)={modulate}";
            }
            RestoreLogger.Info($"[Diag] NPlayerHand: {handInfo}");

            string aqInfo = "null";
            if (rm != null)
            {
                var aqEmpty = rm.ActionQueueSet?.IsEmpty;
                var sync = rm.ActionQueueSynchronizer?.CombatState;
                aqInfo = $"ActionQueueSet.IsEmpty={aqEmpty} ActionQueueSynchronizer.CombatState={sync}";
            }
            RestoreLogger.Info($"[Diag] RunManager: {aqInfo}");

            // Per-card visibility/modulate.
            if (hand != null)
            {
                int idx = 0;
                foreach (var holder in hand.ActiveHolders)
                {
                    var nc = holder.CardNode;
                    if (nc == null)
                    {
                        RestoreLogger.Info($"[Diag]  holder[{idx}] CardNode=null");
                    }
                    else
                    {
                        var alpha = nc.Modulate.A;
                        var visible = nc.Visible;
                        var mf = (nc as Control)?.MouseFilter;
                        RestoreLogger.Info($"[Diag]  holder[{idx}] card={nc.Model?.Id} visible={visible} alpha={alpha:F2} MouseFilter={mf}");
                    }
                    idx++;
                }
            }
        }
        catch (Exception ex)
        {
            RestoreLogger.Warn($"[Diag] dump failed: {ex.Message}");
        }
    }
}
