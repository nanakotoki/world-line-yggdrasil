using System.IO;
using MegaCrit.Sts2.Core.Logging;

namespace WorldLineYggdrasil.Restore;

/// <summary>
/// Logger for the restore subsystem (adapted from the MIT-licensed
/// sts2-undo-mod UndoLogger). Wraps the game logger and also mirrors output
/// to %APPDATA%/SlayTheSpire2/WorldLineYggdrasil/restore.log.
/// </summary>
public static class RestoreLogger
{
    public const bool EnableInfoLogging = false;

    private static readonly object FileLock = new();
    private static string? _logPath;

    public static string LogPath
    {
        get
        {
            if (_logPath != null) return _logPath;
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SlayTheSpire2", "WorldLineYggdrasil");
            Directory.CreateDirectory(dir);
            _logPath = Path.Combine(dir, "restore.log");
            return _logPath;
        }
    }

    public static void TruncateLog()
    {
        try { lock (FileLock) File.WriteAllText(LogPath, string.Empty); } catch { }
    }

    public static void Info(string msg)
    {
        if (!EnableInfoLogging) return;
        Log.Info("[WorldLineYggdrasil.Restore] " + msg);
        Append("INFO", msg);
    }

    public static void Warn(string msg)
    {
        Log.Warn("[WorldLineYggdrasil.Restore] " + msg);
        Append("WARN", msg);
    }

    public static void Debug(string msg)
    {
        if (!EnableInfoLogging) return;
        Log.Info("[WorldLineYggdrasil.Restore] " + msg);
        Append("DEBUG", msg);
    }

    public static void Probe(string category, string msg)
    {
        if (!EnableInfoLogging) return;
        Append("PROBE/" + category, msg);
    }

    private static void Append(string level, string msg)
    {
        try
        {
            lock (FileLock)
            {
                File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {level} {msg}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }
}