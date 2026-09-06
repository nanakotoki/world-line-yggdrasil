using System.Text.Json;
using WorldLineYggdrasil.Graph;

namespace WorldLineYggdrasil.Persistence;

/// <summary>
/// Writes the current combat graph to disk as JSON so the explored state space
/// can be inspected and (later) reloaded across sessions.
/// Output root: %APPDATA%\SlayTheSpire2\WorldLineYggdrasil\graphs\
/// </summary>
public static class GraphExporter
{
    public static string? Export(CombatGraph graph, string tag)
    {
        try
        {
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SlayTheSpire2", "WorldLineYggdrasil", "graphs");
            Directory.CreateDirectory(root);
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
            string safeTag = Sanitize(tag);
            string path = Path.Combine(root, $"{safeTag}_{stamp}.json");

            var payload = new
            {
                scope = graph.Scope,
                root_node = graph.RootNodeId,
                current_node = graph.CurrentNodeId,
                nodes = graph.Nodes.Select(n => new
                {
                    id = n.Id,
                    name = n.Name,
                    terminal = n.IsTerminal,
                    terminal_kind = n.TerminalKind,
                    merge_count = n.MergeCount,
                    label = n.Canonical,
                    snapshot = n.Snapshot,
                }),
                edges = graph.Edges.Select(e => new
                {
                    from = e.FromId,
                    to = e.ToId,
                    action = e.Action,
                    target = e.TargetId,
                }),
            };

            string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
            });
            File.WriteAllText(path, json);
            return path;
        }
        catch (Exception ex)
        {
            MegaCrit.Sts2.Core.Logging.Log.Error($"[WorldLineYggdrasil] graph export failed: {ex.Message}");
            return null;
        }
    }

    private static string Sanitize(string s)
    {
        var chars = s.ToCharArray().Select(c => char.IsLetterOrDigit(c) ? c : '_');
        string result = new string(chars.ToArray());
        return string.IsNullOrWhiteSpace(result) ? "graph" : result;
    }
}