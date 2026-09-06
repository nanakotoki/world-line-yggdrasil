using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Runs;
using WorldLineYggdrasil.Capture;

namespace WorldLineYggdrasil;

[ModInitializer(nameof(Initialize))]
public static class ModEntry
{
    public const string ModId = "WorldLineYggdrasil";
    public const string Version = "0.0.1";

    private static bool _initialized;

    /// <summary>Singleton recorder, also exposed for the graph UI (M2).</summary>
    public static CombatRecorderModel Recorder { get; } = new();

    /// <summary>Run-level (big node) recorder.</summary>
    public static Capture.RunRecorderModel RunRecorder { get; } = new();

    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }
        _initialized = true;

        Log.Info($"[{ModId}] Initialize v{Version}");
        try
        {
            // Recorder first: even if a Harmony patch target drifts across game
            // updates, combat recording keeps working.
            ModHelper.SubscribeForRunStateHooks("WorldLineYggdrasil.run", _ => new[] { Recorder });
            ModHelper.SubscribeForCombatStateHooks("WorldLineYggdrasil.combat", _ => new[] { Recorder });
            ModHelper.SubscribeForRunStateHooks("WorldLineYggdrasil.runrec", _ => new[] { RunRecorder });

            var harmony = new Harmony($"github.worldlineyggdrasil.{ModId}");
            harmony.PatchAll(Assembly.GetExecutingAssembly());

            Ui.WlyOverlayHost.Install();

            Log.Info($"[{ModId}] Harmony patches installed; combat recorder subscribed");
        }
        catch (Exception ex)
        {
            Log.Error($"[{ModId}] Initialize failed: {ex}");
        }
        Log.Info($"[{ModId}] Ready");
    }
}