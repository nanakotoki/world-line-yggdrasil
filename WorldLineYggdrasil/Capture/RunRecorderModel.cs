using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using WorldLineYggdrasil.Graph;

namespace WorldLineYggdrasil.Capture;

/// <summary>
/// Records the run-level "big node" tree: every room / event boundary becomes a
/// big node with a captured run save (for run-level restore), and each combat
/// big node is linked to its combat sub-graph (small nodes). Branches appear
/// when the player restores to an earlier big node and takes a different path.
/// </summary>
public sealed class RunRecorderModel : AbstractModel
{
    public const string LogTag = "[WorldLineYggdrasil.Run]";

    public RunGraph Graph { get; private set; } = new();

    /// <summary>Seed of the current run; a change marks a brand-new run.</summary>
    private string _runSeed = "";

    public override bool ShouldReceiveCombatHooks => false;

    /// <summary>Room -> label map for nicer run node labels.</summary>
    private static readonly Dictionary<string, string> RoomLabels = new()
    {
        ["Monster"] = "战斗",
        ["Elite"] = "精英战",
        ["Boss"] = "Boss战",
        ["Event"] = "事件",
        ["RestSite"] = "休息点",
        ["Shop"] = "商店",
        ["Treasure"] = "宝箱",
        ["Unassigned"] = "?",
    };

    public override Task BeforeRoomEntered(AbstractRoom room)
    {
        try
        {
            CaptureRunNode(room);
        }
        catch (Exception ex)
        {
            Log.Warn($"{LogTag} BeforeRoomEntered failed: {ex.Message}");
        }
        return Task.CompletedTask;
    }

    public override Task AfterCombatVictory(CombatRoom room)
    {
        MarkCombatTerminal("Victory");
        return Task.CompletedTask;
    }

    public override Task AfterCombatEnd(CombatRoom room)
    {
        // 小图已取消：不再把战斗小图链接到大结点（CombatScope 不再设置）。
        return Task.CompletedTask;
    }

    private void CaptureRunNode(AbstractRoom room)
    {
        string roomType = room.RoomType.ToString();
        var run = RunManager.Instance.DebugOnlyGetState();
        int floor = run?.ActFloor ?? 0;
        int act = run?.CurrentActIndex ?? 0;

        // A NEW run (different seed) must start a fresh world-line tree; otherwise
        // the previous run's big nodes / archived combat graphs leak into this one.
        string seed = run?.Rng?.StringSeed ?? "";
        if (seed != "" && seed != _runSeed)
        {
            _runSeed = seed;
            Graph.Reset(seed);
            ModEntry.Recorder.ClearArchivedGraphs();
            Restore.RestoreService.Reset();
            Log.Info($"{LogTag} new run detected (seed {seed}); world-line tree reset");
        }

        if (Graph.Seed == "")
        {
            Graph.Reset(seed != "" ? seed : $"act{act}");
            Log.Info($"{LogTag} run tree started");
        }

        // Big-node identity must distinguish the PATH taken (flying vs walking,
        // different map choices), not just act/floor/type — otherwise two different
        // choices that land on the same room type at the same floor MERGE into one
        // node and their saved states (e.g. WingedBoots.TimesUsed) contaminate each
        // other. The visited-coords path is the world-line identity.
        string path = run != null ? string.Join(",", run.VisitedMapCoords.Select(c => $"{c.col}:{c.row}")) : "";
        string id = $"{act}-{floor}-{roomType}-{PathHash(path)}";
        var node = new RunNode
        {
            Id = id,
            RoomType = roomType,
            Floor = floor,
            Label = $"第{act + 1}幕·{RoomLabels.GetValueOrDefault(roomType, roomType)}",
        };

        // capture run save for run-level restore. AbstractRoom.FromSerializable
        // only supports Monster/Elite/Boss/Event; for other rooms (Shop, Rest,
        // Treasure) pass null so LoadRun creates a fresh room for the map point.
        try
        {
            bool supportsPreFinished = room is CombatRoom or EventRoom;
            node.Save = RunManager.Instance.ToSave(supportsPreFinished ? room : null);
        }
        catch (Exception ex)
        {
            Log.Warn($"{LogTag} run save capture failed: {ex.Message}");
        }

        var existing = Graph.GetNode(id);
        bool isNew = existing == null;
        var stored = Graph.AddOrUpdateNode(node);

        if (Graph.CurrentNodeId != null && Graph.CurrentNodeId != stored.Id)
        {
            Graph.AddEdge(Graph.CurrentNodeId, stored.Id, "进入" + RoomLabels.GetValueOrDefault(roomType, roomType));
        }
        Graph.SetCurrent(stored.Id);
        Graph.NotifyChanged();

        Log.Info($"{LogTag} big node {(isNew ? "new" : "merged")} {id} floor={floor} type={roomType}");
    }

    private void MarkCombatTerminal(string kind)
    {
        if (Graph.CurrentNodeId is { } id)
        {
            var node = Graph.GetNode(id);
            if (node != null)
            {
                node.IsTerminal = true;
                node.TerminalKind = kind;
                Graph.NotifyChanged();
            }
        }
    }

    /// <summary>Stable short hash of the visited-coords path (part of a big node id).</summary>
    private static string PathHash(string path)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    private void LinkCombatGraph()
    {
        var archive = ModEntry.Recorder.ArchivedGraphs.LastOrDefault();
        var live = ModEntry.Recorder.Graph;
        var graph = archive ?? (live.Scope != "" && live.Nodes.Count > 0 ? live : null);
        if (graph == null || Graph.CurrentNodeId is not { } id)
        {
            return;
        }
        var node = Graph.GetNode(id);
        if (node != null)
        {
            node.CombatScope = graph.Scope;
            Graph.NotifyChanged();
            Log.Info($"{LogTag} combat graph '{graph.Scope}' linked to big node {id}");
        }
    }

    public void SetCurrent(string nodeId)
    {
        Graph.SetCurrent(nodeId);
    }
}