using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using WorldLineYggdrasil.Graph;

namespace WorldLineYggdrasil.Restore;

/// <summary>
/// Restores the whole run to a recorded "big node" (room boundary) by loading
/// the run save captured at that node. Uses the same path as the game's
/// FileDropHandler debug feature.
/// </summary>
public static class RunRestoreService
{
    public static async Task<bool> RestoreTo(RunNode node)
    {
        if (node?.Save == null)
        {
            return false;
        }
        if (RunManager.Instance == null || NGame.Instance == null)
        {
            return false;
        }
        try
        {
            // Reset the run recorder so the re-entered room merges back into
            // this big node (and future choices branch from it).
            ModEntry.RunRecorder.SetCurrent(node.Id);

            var runManager = RunManager.Instance;
            var savedRun = node.Save;

            // Suppress the card fly VFX (deal/draw animation) while the run
            // reloads into a combat (the semantic restore settles right after).
            Search.SearchSpeed.SuppressCardTweens = true;

            runManager.CleanUp();
            var loadedState = RunState.FromSerializable(savedRun);
            await runManager.SetUpSavedSingleplayer(loadedState, savedRun);
            NGame.Instance.ReactionContainer.InitializeNetworking(new NetSingleplayerGameService());
            await NGame.Instance.LoadRun(loadedState, savedRun.PreFinishedRoom);

            RestoreLogger.Info($"[RunRestore] restored run to big node {node.Id} ({node.Label})");
            return true;
        }
        catch (Exception ex)
        {
            RestoreLogger.Warn($"[RunRestore] failed for node {node.Id}: {ex}");
            return false;
        }
    }
}