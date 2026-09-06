using System.Security.Cryptography;
using System.Text;

namespace WorldLineYggdrasil.Graph;

/// <summary>
/// Produces a canonical string and a stable fingerprint for a combat state.
/// The canonical string is deterministic given the semantic fields (RNG ignored),
/// so the same situation always maps to the same node id.
/// </summary>
public static class Canonicalizer
{
    public static string CanonicalString(CombatSnapshotData s)
    {
        var sb = new StringBuilder();
        sb.Append("r=").Append(s.RoundNumber).Append(';');
        sb.Append("t=").Append(s.TurnNumber).Append(';');
        sb.Append("side=").Append(s.Side).Append(';');
        sb.Append("hp=").Append(s.PlayerHp).Append('/').Append(s.PlayerMaxHp).Append(';');
        sb.Append("block=").Append(s.PlayerBlock).Append(';');
        sb.Append("e=").Append(s.Energy).Append(';');
        sb.Append("s=").Append(s.Stars).Append(';');
        sb.Append("g=").Append(s.Gold).Append(';');
        sb.Append("me=").Append(s.MaxEnergy).Append(';');
        AppendDict(sb, "pp", s.PlayerPowers);
        sb.Append("hand=").Append(string.Join(",", s.Hand)).Append(';');
        sb.Append("draw=").Append(string.Join(",", s.Draw)).Append(';');
        sb.Append("discard=").Append(string.Join(",", s.Discard)).Append(';');
        sb.Append("exhaust=").Append(string.Join(",", s.Exhaust)).Append(';');
        sb.Append("play=").Append(string.Join(",", s.Play)).Append(';');
        sb.Append("relics=").Append(string.Join(",", s.Relics.OrderBy(x => x))).Append(';');
        sb.Append("potions=").Append(string.Join(",", s.Potions.OrderBy(x => x))).Append(';');

        var enemies = s.Enemies
            .OrderBy(e => e.Id, StringComparer.Ordinal)
            .ThenBy(e => e.Hp)
            .ToList();
        sb.Append("enemies=[");
        for (int i = 0; i < enemies.Count; i++)
        {
            var e = enemies[i];
            if (i > 0) sb.Append(',');
            sb.Append(e.Id).Append('(').Append("hp=").Append(e.Hp).Append(';');
            sb.Append("block=").Append(e.Block).Append(';');
            sb.Append("intent=").Append(e.Intent).Append(';');
            AppendDict(sb, "", e.Powers);
            sb.Append(')');
        }
        sb.Append(']');
        return sb.ToString();
    }

    public static string Fingerprint(CombatSnapshotData s)
    {
        string canonical = CanonicalString(s);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    private static void AppendDict(StringBuilder sb, string label, Dictionary<string, int> dict)
    {
        sb.Append(label);
        if (dict.Count == 0)
        {
            sb.Append("=-;");
            return;
        }
        sb.Append('{');
        bool first = true;
        foreach (var kv in dict.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(kv.Key).Append(':').Append(kv.Value);
        }
        sb.Append("};");
    }
}