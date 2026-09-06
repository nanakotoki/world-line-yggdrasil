using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using WorldLineYggdrasil.Graph;

namespace WorldLineYggdrasil.Restore;

/// <summary>
/// Holds a deep <see cref="CombatSnapshot"/> per recorded graph node for the
/// current combat, and restores the live game to a chosen node.
/// </summary>
public static class RestoreService
{
    private static readonly Dictionary<string, CombatSnapshot> Snapshots = new();
    private static readonly Queue<string> Order = new();
    private static bool _suspended;

    /// <summary>
    /// Hard cap on retained deep snapshots. Each snapshot deep-clones the whole
    /// combat (~50-200 KB); unbounded growth during a large auto-search would
    /// eat hundreds of MB. Past the cap the OLDEST snapshots are evicted (the
    /// search only re-restores recent frontier nodes, so coverage is barely hit).
    /// </summary>
    public const int MaxSnapshots = 1500;
    private static bool _evictWarned;

    /// <summary>
    /// Deep snapshot capture is disabled while a restore is being applied (the
    /// recorder must not race a mid-restore state).
    /// </summary>
    public static bool IsRestoring { get; private set; }

    /// <summary>
    /// Semantic snapshot to apply when the next combat's first player turn
    /// starts (set before a big-node restore re-enters a combat room, so the
    /// player lands at a specific recorded step of that combat).
    /// </summary>
    public static CombatSnapshotData? PendingSemanticRestore { get; private set; }

    public static void SetPendingSemanticRestore(CombatSnapshotData data) => PendingSemanticRestore = data;

    public static void ClearPendingSemanticRestore() => PendingSemanticRestore = null;

    public static bool Enabled { get; set; } = true;

    public static int SnapshotCount => Snapshots.Count;

    public static void Reset()
    {
        Snapshots.Clear();
        Order.Clear();
    }

    /// <summary>
    /// Captures a deep snapshot of the live combat and stores it for a node.
    /// Capture is wrapped so a failure here never propagates into the game's
    /// hook/turn loop.
    /// </summary>
    public static void Store(string nodeId)
    {
        if (!Enabled || IsRestoring || _suspended)
        {
            return;
        }
        if (!CombatManager.Instance.IsInProgress)
        {
            return;
        }
        try
        {
            var snap = CombatSnapshot.Capture();
            if (snap != null)
            {
                if (!Snapshots.ContainsKey(nodeId))
                {
                    Order.Enqueue(nodeId);
                }
                Snapshots[nodeId] = snap;
                while (Order.Count > MaxSnapshots)
                {
                    string oldest = Order.Dequeue();
                    Snapshots.Remove(oldest);
                    if (!_evictWarned)
                    {
                        _evictWarned = true;
                        RestoreLogger.Warn($"[RestoreService] deep-snapshot cap {MaxSnapshots} reached; evicting oldest. Older nodes may no longer be restorable.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            RestoreLogger.Warn($"[RestoreService] deep capture failed for node {nodeId}: {ex}");
        }
    }

    /// <summary>
    /// Schedules a deep capture on the next frame so it runs after the game's
    /// hook chain has fully settled (safe from turn-start transitions).
    /// </summary>
    public static void StoreDeferred(string nodeId)
    {
        if (!Enabled || IsRestoring || _suspended)
        {
            return;
        }
        Godot.Callable.From(() => Store(nodeId)).CallDeferred();
    }

    public static bool CanRestore(string nodeId) => Snapshots.ContainsKey(nodeId);

    /// <summary>
    /// Cancels any open card-selection screens (e.g. a potion's "choose a card"
    /// overlay). Restoring the model leaves such overlays orphaned — the potion
    /// reverts but the selection screen stays, letting the player use it again.
    /// Freeing the screen triggers its _ExitTree → completion-source cancel, which
    /// resolves the pending player choice and unblocks the action queue.
    /// </summary>
    public static void CancelOpenCardSelections()
    {
        try
        {
            var type = AccessTools.TypeByName(
                "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCombatPileCardSelectScreen");
            if (type == null)
            {
                return;
            }
            if (Engine.GetMainLoop() is not SceneTree tree || tree.Root == null)
            {
                return;
            }
            foreach (var node in Enumerate(tree.Root, type))
            {
                try { node.QueueFree(); }
                catch { }
            }
        }
        catch (Exception ex)
        {
            RestoreLogger.Warn($"[RestoreService] cancel selections failed: {ex.Message}");
        }
    }

    private static System.Collections.Generic.IEnumerable<Node> Enumerate(Node root, System.Type targetType)
    {
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (targetType.IsInstanceOfType(n))
            {
                yield return n;
            }
            foreach (var c in n.GetChildren())
            {
                stack.Push(c);
            }
        }
    }

    public static bool RestoreTo(string nodeId)
    {
        if (!Snapshots.TryGetValue(nodeId, out var snap))
        {
            return false;
        }
        if (!CombatManager.Instance.IsInProgress)
        {
            return false;
        }
        // Restoring mid-enemy-turn races in-flight draw/intent resolution and
        // can over-fill the hand; only allow restore during the player's turn.
        if (CombatManager.Instance.IsEnemyTurnStarted)
        {
            return false;
        }
        IsRestoring = true;
        try
        {
            CancelOpenCardSelections();
            SnapshotRestorer.Restore(snap);
            return true;
        }
        catch (Exception ex)
        {
            RestoreLogger.Warn($"[RestoreService] restore failed: {ex}");
            return false;
        }
        finally
        {
            IsRestoring = false;
        }
    }

    /// <summary>
    /// Used by the recorder to pause deep capture while graph bookkeeping is
    /// happening or when combat is ending.
    /// </summary>
    public static IDisposable Suspend()
    {
        _suspended = true;
        return new Releaser();
    }

    private sealed class Releaser : IDisposable
    {
        public void Dispose() => _suspended = false;
    }
}