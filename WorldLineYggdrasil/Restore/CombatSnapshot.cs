using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Orbs;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using System.Reflection;

namespace WorldLineYggdrasil.Restore;

/// <summary>
/// Captured combat state for a single undo step. Restore() lands in the next phase.
///
/// Differences from reference impl (luojiesi/SLS2Mods/UndoAndRedo) — these target
/// the bugs the user reported (Stone Sword, Statue, Centipede, Slimes, 3-phase boss):
///   1. Per-creature: HadVisualNode + position/scale captured → dead→alive restore
///      can rebuild NCreature at the right place.
///   2. PowerModel._internalData → DeepCloner (recursive reflection clone), not
///      MemberwiseClone. Fixes Statue scaling persistence.
///   3. Relic state: every private field per subtype dumped via reflection
///      (FullFieldDump). Reference impl restores only DynamicVars + StackCount.
/// </summary>
internal sealed class CombatSnapshot
{
    /// <summary>
    /// Creature → most recently observed *stable* (loop/idle) Spine track-0 name.
    /// Updated whenever capture sees a stable anim, used as fallback when the
    /// snapshot lands mid-transient (attack/hurt/cast/etc.) so the snap still
    /// records a sensible pose. Keyed by Creature reference (CombatId can be 0
    /// for the player, which would collide). Cleared on combat end.
    /// </summary>
    public static readonly Dictionary<Creature, string> IdleAnimCache =
        new(ReferenceEqualityComparer.Instance);

    public bool IsTurnBoundary { get; init; }
    public DateTime CapturedAt { get; } = DateTime.UtcNow;

    public int RoundNumber;
    public int TurnNumber;
    public CombatSide CurrentSide;

    public int Energy;
    public int Stars;
    public int Gold;

    public Dictionary<PileType, List<CardModel>> PileRefs = new();
    public Dictionary<CardModel, CardModel> CardMutableClones = new(ReferenceEqualityComparer.Instance);
    public List<CardModel> AllCardRefs = new();

    public uint NextCreatureId;
    public List<CreatureSnapshot> Creatures = new();
    public List<uint> PetCombatIds = new();
    public List<Creature> EscapedCreatures = new();

    public List<RelicSnapshot> Relics = new();

    public List<PotionModel?> PotionSlotRefs = new();
    public Dictionary<PotionModel, PotionModel> PotionClones = new(ReferenceEqualityComparer.Instance);

    // Orbs — refs preserved (NOrb visual binds to OrbModel identity); clones hold
    // pre-action mutable state (DarkOrb._evokeVal, GlassOrb._passiveVal, etc.).
    // Capacity captured separately because evolving relics (e.g. orb-slot upgrade)
    // can change it mid-combat.
    public bool HasOrbData;
    public int OrbCapacity;
    public List<OrbModel> OrbRefs = new();
    public Dictionary<OrbModel, OrbModel> OrbClones = new(ReferenceEqualityComparer.Instance);

    public Dictionary<RunRngType, SerializableRng> RunRngs = new();

    public List<object>? HistoryEntries;

    public ActionSynchronizerCombatState SyncCombatState;
    public bool CombatManagerPaused;

    /// <summary>
    /// Identity-set of nodes that were children-of-children under NCombatRoom at
    /// capture time. At restore, anything NOT in this set is presumed to be an
    /// ephemeral VFX (flying card silhouette, particle, damage number, etc.)
    /// that the in-flight action spawned and free-able.
    /// </summary>
    public HashSet<Godot.Node> SceneNodes = new(ReferenceEqualityComparer.Instance);

    // ─── Capture ───

    public static CombatSnapshot? Capture(bool isTurnBoundary = false)
    {
        var cm = CombatManager.Instance;
        if (cm == null) return null;
        var cs = ReflectionCache.GetCombatState(cm);
        if (cs == null) return null;

        var runState = ReflectionCache.RunManagerStateProperty?.GetValue(RunManager.Instance) as RunState;
        if (runState == null) return null;

        var snap = new CombatSnapshot { IsTurnBoundary = isTurnBoundary };

        // Per-stage timing — only emitted when total capture exceeds 100ms,
        // so we can profile where the cost is on slow captures without
        // adding overhead to fast paths. Stage timings written as a single
        // Warn line (always lands in probe.log).
        long t0 = System.Environment.TickCount64;
        long tCombat, tPlayer, tCreatures, tRng, tHistory, tSync;
        try
        {
            CaptureCombatLevel(snap, cs);            tCombat    = System.Environment.TickCount64;
            CapturePlayerAndPiles(snap, cs);         tPlayer    = System.Environment.TickCount64;
            CaptureCreatures(snap, cs);              tCreatures = System.Environment.TickCount64;
            CaptureRunRng(snap, runState);           tRng       = System.Environment.TickCount64;
            CaptureHistory(snap, cm);                tHistory   = System.Environment.TickCount64;
            CaptureSyncState(snap);                  tSync      = System.Environment.TickCount64;
            // CaptureSceneNodes intentionally disabled — walking the entire
            // NCombatRoom subtree (~1000+ nodes) on every card play caused
            // visible frame drops. EphemeralNodeCleaner becomes a no-op for
            // snapshots without a baseline, so post-undo VFX may briefly
            // persist but auto-cleans up via game's normal node lifetimes.
        }
        catch (Exception ex)
        {
            RestoreLogger.Warn($"[Snapshot] capture failed: {ex.Message}");
            return null;
        }

        long total = System.Environment.TickCount64 - t0;
        if (total >= 100)
        {
            RestoreLogger.Warn(
                $"[Snapshot] STAGE-TIMING total={total}ms " +
                $"combat={tCombat - t0}ms " +
                $"player={tPlayer - tCombat}ms " +
                $"creatures={tCreatures - tPlayer}ms " +
                $"rng={tRng - tCreatures}ms " +
                $"history={tHistory - tRng}ms " +
                $"sync={tSync - tHistory}ms");
        }

        // Per-snapshot summary log was firing on every card play (≥1 disk
        // write per action) and contributed to frame stutter. Removed to
        // keep the hot path quiet; capture failures are still logged via
        // the catch above, and undo logs `[Restore] start → ...` at restore.
        return snap;
    }

    private static void CaptureCombatLevel(CombatSnapshot snap, CombatState cs)
    {
        snap.RoundNumber = cs.RoundNumber;
        snap.CurrentSide = cs.CurrentSide;
        snap.NextCreatureId = (uint?)ReflectionCache.NextCreatureIdField?.GetValue(cs) ?? 0u;

        if (ReflectionCache.AllCardsField?.GetValue(cs) is List<CardModel> all)
            snap.AllCardRefs.AddRange(all);

        try { snap.EscapedCreatures.AddRange(cs.EscapedCreatures); } catch { }
    }

    private static void CapturePlayerAndPiles(CombatSnapshot snap, CombatState cs)
    {
        long tStart = System.Environment.TickCount64;
        long tCards = 0, tRelics = 0, tPotions = 0;
        int cardCount = 0, relicCount = 0, potionCount = 0;

        // Combat-side allies include the player creature(s).
        foreach (var ally in cs.Allies)
        {
            var player = ally.Player;
            if (player == null) continue;

            var pcs = player.PlayerCombatState;
            if (pcs != null)
            {
                snap.Energy = (int)(ReflectionCache.PcsEnergyField.GetValue(pcs) ?? 0);
                snap.Stars = (int)(ReflectionCache.PcsStarsField.GetValue(pcs) ?? 0);
                snap.TurnNumber = pcs.TurnNumber;

                foreach (var pile in pcs.AllPiles)
                    snap.PileRefs[pile.Type] = pile.Cards.ToList();

                long t1 = System.Environment.TickCount64;
                foreach (var card in pcs.AllCards)
                {
                    cardCount++;
                    if (!snap.CardMutableClones.ContainsKey(card))
                        snap.CardMutableClones[card] = (CardModel)card.MutableClone();
                }
                tCards = System.Environment.TickCount64 - t1;

                if (ReflectionCache.PcsPetsField?.GetValue(pcs) is System.Collections.IEnumerable pets)
                    foreach (var p in pets)
                        if (p is Creature c && c.CombatId.HasValue)
                            snap.PetCombatIds.Add(c.CombatId.Value);

                CaptureOrbs(snap, pcs);

                // Diagnostic: dump hand cards' _localModifiers presence so we
                // can verify Pounce/Unrelenting-style local cost modifiers
                // round-trip through capture+restore. Pairs with restore-side
                // [CardMods] line.
                LogHandLocalModifiersAtCapture(pcs);
            }

            // Relics
            long t2 = System.Environment.TickCount64;
            foreach (var relic in player.Relics)
            {
                relicCount++;
                snap.Relics.Add(CaptureRelic(relic));
            }
            tRelics = System.Environment.TickCount64 - t2;

            // Potions
            long t3 = System.Environment.TickCount64;
            for (int i = 0; i < player.PotionSlots.Count; i++)
            {
                var slot = player.PotionSlots[i];
                snap.PotionSlotRefs.Add(slot);
                if (slot != null && !snap.PotionClones.ContainsKey(slot))
                {
                    potionCount++;
                    snap.PotionClones[slot] = (PotionModel)slot.MutableClone();
                }
            }
            tPotions = System.Environment.TickCount64 - t3;

            snap.Gold = (int)(ReflectionCache.PlayerGoldField?.GetValue(player) ?? 0);

            break; // single-player only — first ally with a player object is enough
        }

        long total = System.Environment.TickCount64 - tStart;
        if (total >= 50)
            RestoreLogger.Warn(
                $"[Snapshot] PLAYER-DETAIL total={total}ms " +
                $"cards={tCards}ms ({cardCount}) " +
                $"relics={tRelics}ms ({relicCount}) " +
                $"potions={tPotions}ms ({potionCount})");
    }

    private static void LogHandLocalModifiersAtCapture(PlayerCombatState pcs)
    {
        if (ReflectionCache.CardEnergyCostLocalModifiersField == null) return;
        try
        {
            CardPile? hand = null;
            foreach (var pile in pcs.AllPiles)
                if (pile.Type == PileType.Hand) { hand = pile; break; }
            if (hand == null) return;

            var entries = new List<string>();
            foreach (var card in hand.Cards)
            {
                var energyCost = ReflectionCache.CardEnergyCostProp?.GetValue(card);
                if (energyCost == null) continue;
                var mods = ReflectionCache.CardEnergyCostLocalModifiersField.GetValue(energyCost)
                    as System.Collections.IList;
                int n = mods?.Count ?? 0;
                if (n == 0) continue;
                entries.Add($"{card.Id.Entry}(mods={n})");
            }
            if (entries.Count > 0)
                RestoreLogger.Info($"[CardMods] hand at capture: [{string.Join(", ", entries)}]");
        }
        catch (Exception ex) { RestoreLogger.Warn($"[CardMods] capture log: {ex.Message}"); }
    }

    private static void CaptureOrbs(CombatSnapshot snap, PlayerCombatState pcs)
    {
        var orbQueue = pcs.OrbQueue;
        if (orbQueue == null) return;

        snap.HasOrbData = true;
        snap.OrbCapacity = orbQueue.Capacity;
        foreach (var orb in orbQueue.Orbs)
        {
            snap.OrbRefs.Add(orb);
            if (!snap.OrbClones.ContainsKey(orb))
                snap.OrbClones[orb] = (OrbModel)orb.MutableClone();
        }
    }

    private static RelicSnapshot CaptureRelic(RelicModel rm)
    {
        // Use the game's own MutableClone — it does MemberwiseClone (native,
        // fast) then chains DeepCloneFields overrides that handle each
        // RelicModel subclass's mutable state correctly (DynamicVars,
        // subtype-specific fields). Replaces the previous FullFieldDump +
        // DeepCloner.CloneObject path which was 78-312ms per capture across
        // 18 relics — the dominant frame-drop source on card play.
        // Restore-side reads the clone's fields back onto the live instance
        // (see SnapshotRestorer.RestoreRelics).
        RelicModel? clone = null;
        try { clone = (RelicModel)rm.MutableClone(); }
        catch (Exception ex) { RestoreLogger.Warn($"[Snapshot] relic MutableClone failed: {ex.Message}"); }

        return new()
        {
            Ref = rm,
            Id = rm.Id,
            StackCount = (int)(ReflectionCache.RelicStackCountField?.GetValue(rm) ?? 0),
            Status = ReflectionCache.RelicStatusProperty?.GetValue(rm),
            // DynamicVars are already deep-cloned *inside* MutableClone
            // (RelicModel.DeepCloneFields → DynamicVars.Clone, the game's own
            // per-var compiled clone), and RestoreRelics copies that clone's
            // _dynamicVars field back onto the live relic. The separate
            // reflection-based DeepCloner.CloneObject below re-walked the full
            // ~28-entry DynamicVarSet graph for every relic on every card play —
            // the dominant snapshot-capture cost (issue #2: 63-188ms / 21 relics,
            // ~100% of slow captures). Only compute it as a fallback for the rare
            // case MutableClone threw and left no clone for the field-copy to use.
            DynamicVarsClone = clone == null
                ? DeepCloner.CloneObject(ReflectionCache.RelicDynamicVarsField?.GetValue(rm))
                : null,
            Clone = clone,
        };
    }

    private static bool _baselineDumped;

    private static void CaptureCreatures(CombatSnapshot snap, CombatState cs)
    {
        foreach (var c in cs.Creatures)
        {
            snap.Creatures.Add(CaptureCreature(c));
            // Player-side Powers dump on capture — pairs with the restore-side
            // dump in SnapshotRestorer.RestoreCreaturePowers. Lets us trace
            // cost-modifier powers (FreeSkillPower from Pounce, FreeAttackPower,
            // FreePowerPower) across the full snapshot/restore lifecycle so
            // the user-reported "Pounce + Undo breaks 0-cost" pattern is
            // visible in undo.log without needing repro instrumentation.
            if (c.Side == CombatSide.Player)
            {
                var summary = new List<string>();
                foreach (var pm in c.Powers)
                {
                    var amt = ReflectionCache.PowerAmountField.GetValue(pm);
                    summary.Add($"{pm.Id.Entry}={amt}");
                }
                if (summary.Count > 0)
                    RestoreLogger.Info($"[Powers] player at capture: [{string.Join(", ", summary)}]");
            }
        }

        // Once per game session, dump the tree of a known-alive enemy so we
        // have a reference to compare against the revive shell. Without a
        // baseline, "spineNodes=0" / "descendants=86" is meaningless — we don't
        // know what a healthy enemy looks like.
        if (!_baselineDumped)
        {
            try
            {
                var room = NCombatRoom.Instance;
                if (room != null)
                {
                    foreach (var c in cs.Creatures)
                    {
                        if (c == null || c.Side != CombatSide.Enemy) continue;
                        var nc = room.GetCreatureNode(c);
                        if (nc != null)
                        {
                            RestoreLogger.Info($"[Baseline] healthy enemy id={c.CombatId} dump:");
                            SnapshotRestorer.DumpHealthyCreatureBaseline(nc, c.CombatId ?? 0u);
                            _baselineDumped = true;
                            break;
                        }
                    }
                }
            }
            catch (Exception ex) { RestoreLogger.Warn($"[Baseline] dump failed: {ex.Message}"); }
        }
    }

    private static CreatureSnapshot CaptureCreature(Creature c)
    {
        var snap = new CreatureSnapshot
        {
            Ref = c,
            CombatId = c.CombatId ?? 0u,
            CurrentHp = (int)(ReflectionCache.CreatureHpField.GetValue(c) ?? 0),
            MaxHp = (int)(ReflectionCache.CreatureMaxHpField.GetValue(c) ?? 0),
            Block = (int)(ReflectionCache.CreatureBlockField.GetValue(c) ?? 0),
            IsDead = c.IsDead,
        };

        foreach (var pm in c.Powers)
        {
            // MutableClone preserves subtype-specific private fields (e.g.
            // SurroundedPower._facing) that the explicit Amount/internalData
            // capture path misses. Restore-side reads fields back onto live
            // (mirrors RelicSnapshot.Clone). Falls back gracefully on failure.
            PowerModel? pmClone = null;
            try { pmClone = (PowerModel)pm.MutableClone(); }
            catch (Exception ex) { RestoreLogger.Warn($"[Snapshot] power MutableClone failed ({pm.Id.Entry}): {ex.Message}"); }

            snap.Powers.Add(new PowerSnapshot
            {
                Id = pm.Id,
                Amount = (int)(ReflectionCache.PowerAmountField.GetValue(pm) ?? 0),
                AmountOnTurnStart = (int)(ReflectionCache.PowerAmountOnTurnStartField.GetValue(pm) ?? 0),
                SkipNextDurationTick = (bool)(ReflectionCache.PowerSkipField.GetValue(pm) ?? false),
                InternalDataClone = DeepCloner.CloneObject(
                    ReflectionCache.PowerInternalDataField?.GetValue(pm)),
                Ref = pm,
                Clone = pmClone,
            });
        }

        if (c.Monster is { } monster)
        {
            if (ReflectionCache.MonsterRngField?.GetValue(monster) is Rng rng)
                snap.MonsterRng = rng.ToSerializable();
            snap.MonsterMove = CaptureMonsterMove(monster);
            // Capture all subtype-specific bool/int/enum fields. Concrete
            // monsters (CorpseSlug, etc.) carry their own state — e.g. Slug's
            // `_isRavenous` is set to true by RavenousPower.AfterDeath and only
            // reset to false in the StunnedMove delegate. Without restoring,
            // post-undo the slug stays "ravenous" and triggers stale effects
            // on the next turn boundary.
            snap.MonsterFields = CaptureMonsterFields(monster);
        }

        // Visual node — for death/revive restore
        var nCombatRoom = NCombatRoom.Instance;
        if (nCombatRoom != null)
        {
            var nc = nCombatRoom.GetCreatureNode(c);
            if (nc == null && ReflectionCache.NcrRemovingNodesField?.GetValue(nCombatRoom)
                is System.Collections.IEnumerable removing)
            {
                foreach (var item in removing)
                {
                    if (item is NCreature ncreature
                        && ReflectionCache.NCreatureEntityProp?.GetValue(ncreature) is Creature ent
                        && ReferenceEquals(ent, c))
                    {
                        nc = ncreature;
                        break;
                    }
                }
            }
            if (nc != null)
            {
                snap.HadVisualNode = true;
                try { snap.VisualPosition = nc.GlobalPosition; } catch { }
                try { snap.VisualBodyScale = nc.Body?.Scale ?? Vector2.One; } catch { }
                // body.Position is the body's local offset relative to NCreature.
                // AnimDie typically tweens body.Position during the death anim
                // (collapse, slide, etc.). Without restoring, the revived body
                // stays at the mid-death offset and the creature renders far
                // off-screen even though all transforms looked OK.
                try { snap.VisualBodyPosition = nc.Body?.Position ?? Vector2.Zero; } catch { }
                try { snap.VisualBodyRotation = nc.Body?.Rotation ?? 0f; } catch { }
                try
                {
                    if (nc.Body is Godot.CanvasItem bodyCi)
                        snap.VisualBodyModulate = bodyCi.Modulate;
                }
                catch { }

                // Capture the death-tint shader state. Look up NCreatureVisuals
                // child (held on <Visuals>k__BackingField) and read _hue + timer.
                // Also strong-ref the body Node2D — if death anim later frees
                // it from the zombie subtree, we can try to re-attach.
                try
                {
                    var visuals = nc.Visuals;
                    if (visuals != null)
                    {
                        if (ReflectionCache.NCVHueField?.GetValue(visuals) is float h)
                            snap.Hue = h;
                        if (ReflectionCache.NCVLiquidOverlayTimerField?.GetValue(visuals) is double t)
                            snap.LiquidOverlayTimer = t;

                        // Capture the body's "true" normal material. If overlay is
                        // active right now, the live body material is the overlay
                        // shader, not the base — so prefer `_savedNormalMaterial`
                        // (the pre-overlay material the loop will eventually restore).
                        try
                        {
                            var spineProp = ReflectionCache.NCVSpineAnimationProp;
                            // SpineAnimation prop returns a struct holding MegaSprite,
                            // but we want the raw MegaSprite — use SpineBody public prop.
                            var spineBodyProp = HarmonyLib.AccessTools.Property(visuals.GetType(), "SpineBody");
                            var spineBody = spineBodyProp?.GetValue(visuals);
                            var current = spineBody != null
                                ? ReflectionCache.MegaSpriteGetNormalMaterialMethod?.Invoke(spineBody, null) as Godot.Material
                                : null;
                            var saved = ReflectionCache.NCVSavedNormalMaterialField?.GetValue(visuals) as Godot.Material;
                            var overlay = ReflectionCache.NCVCurrentLiquidOverlayMaterialField?.GetValue(visuals) as Godot.Material;
                            snap.LiquidOverlayWasActive = overlay != null;
                            snap.BodyNormalMaterial = snap.LiquidOverlayWasActive ? saved : current;
                        }
                        catch (Exception ex) { RestoreLogger.Warn($"[Snapshot] body material capture: {ex.Message}"); }

                        var bodyField = HarmonyLib.AccessTools.Field(visuals.GetType(), "_body");
                        var body = bodyField?.GetValue(visuals) as Godot.Node;
                        if (body != null)
                        {
                            snap.BodyRef = body;
                            try { snap.BodyParentRef = body.GetParent(); } catch { }
                        }
                    }
                }
                catch { }

                // Pose capture — only trust observed names that look like a
                // stable loop (contain "loop" or "idle"). Everything else is
                // one-shot (attack/hurt/cast/intro/spawn/die/etc.) that must
                // not be restored verbatim. For one-shots, fall back to the
                // last seen stable loop for this creature; if cache empty, null.
                var observed = TryReadSpineAnim(nc);
                // Track 1-3 reads were diagnostic only (to spot stun anims
                // on non-zero tracks). They each invoked SpineAnimation
                // .GetCurrentTrack(N) which throws TargetInvocationException
                // for tracks that don't exist on the skeleton — every
                // capture fired ~9 exceptions across 3 creatures with their
                // wrapped stack traces, dominating capture time (200-500ms
                // per card play). Removed from hot path. If a future stun-
                // pose bug shows up, re-enable temporarily.

                // Capture every observed track-0 anim verbatim — even names
                // that don't look like a loop (e.g. 'stunned', 'down', 'sleep'
                // without the `_loop` suffix) — when they correspond to a
                // sustained pose. We can't tell sustained from one-shot purely
                // by name, so the rule is: if it's "loop-shaped" use it; else
                // fall back to cache; else null. The diag above lets us add
                // discovered new stable names to IsLoopShaped if we miss any.
                if (observed != null && IsLoopShaped(observed))
                {
                    snap.SpineAnimNameTrack0 = observed;
                    if (IsTrueIdleLoop(observed))
                        IdleAnimCache[c] = observed;
                }
                else if (!c.IsDead && IdleAnimCache.TryGetValue(c, out var stable))
                {
                    // Cache fallback only applies when the creature is alive.
                    // For a creature in mid-death (c.IsDead=true but spine still
                    // transient like 'die'/'die_to_dead'), the cached idle would
                    // be the alive-state idle_loop captured earlier. Restoring
                    // that on undo would visually revive a dead creature —
                    // the user-reported "kill, play unrelated card, undo, sees
                    // alive visual" symptom. Leave snap.SpineAnimNameTrack0
                    // null for dead-in-mid-anim, restorer will skip spine.
                    snap.SpineAnimNameTrack0 = stable;
                }
                else
                {
                    snap.SpineAnimNameTrack0 = null;
                }
            }
        }

        return snap;
    }

    /// <summary>
    /// STS2 spine animations follow a convention: stable (loopable) anims have
    /// "loop" or "idle" in the name (idle_loop, block_loop, low_hp_loop). Everything
    /// else (attack, hurt, cast, intro, spawn, die, etc.) is one-shot and not
    /// safe to record as a restorable pose.
    ///
    /// Exception: incapacitated-loop anims (`stunned_loop`, `frozen_loop`, etc.)
    /// also contain "loop" but represent a transient power/affliction effect
    /// that shouldn't be promoted to the cached idle pose — caching them
    /// poisons the fallback so subsequent undos restore the stunned pose
    /// instead of true idle.
    /// </summary>
    /// <summary>
    /// True if the anim name contains "loop" or "idle" — i.e. a loopable
    /// pose worth capturing verbatim. Includes stunned/frozen/etc.
    /// </summary>
    private static bool IsLoopShaped(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower.IndexOf("loop", StringComparison.Ordinal) >= 0
            || lower.IndexOf("idle", StringComparison.Ordinal) >= 0;
    }

    /// <summary>
    /// True if the anim is safe to use as an IdleAnimCache fallback. Excludes
    /// incapacitated loops (stunned/knocked/frozen/sleep/daze) — those are
    /// tied to a transient power and shouldn't be the default pose.
    /// </summary>
    private static bool IsTrueIdleLoop(string name)
    {
        if (!IsLoopShaped(name)) return false;
        var lower = name.ToLowerInvariant();
        if (lower.Contains("stun") || lower.Contains("knock") || lower.Contains("freeze")
            || lower.Contains("sleep") || lower.Contains("daze"))
            return false;
        return true;
    }

    private static bool LooksStableLoop(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.Contains("stun") || lower.Contains("knock") || lower.Contains("freeze")
            || lower.Contains("sleep") || lower.Contains("daze"))
            return false;
        return lower.IndexOf("loop", StringComparison.Ordinal) >= 0
            || lower.IndexOf("idle", StringComparison.Ordinal) >= 0;
    }

    /// <summary>
    /// Returns true if any live creature's track 0 is currently a transient
    /// anim (attack/hurt/cast/etc.). Used as an undo guard — the snapshot
    /// restore needs every creature in a stable loop for the captured-state
    /// approach to land cleanly.
    /// </summary>
    public static bool AnyCreatureMidTransient()
    {
        try
        {
            var cm = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
            if (cm == null) return false;
            var cs = ReflectionCache.GetCombatState(cm);
            if (cs == null) return false;

            foreach (var c in cs.Creatures)
            {
                if (c == null) continue;
                var room = NCombatRoom.Instance;
                var nc = room?.GetCreatureNode(c);
                if (nc == null) continue;
                var observed = TryReadSpineAnim(nc);
                if (observed != null && IsTransientName(observed)) return true;
            }
        }
        catch { }
        return false;
    }

    private static readonly string[] TransientPatterns =
        { "attack", "cast", "hurt", "hit", "damage", "die", "death", "spawn" };

    private static bool IsTransientName(string name)
    {
        // A loop-shaped anim (`die_loop`, `dead_loop`, `stun_loop`, `idle`…) is a
        // SETTLED state, not an in-flight one-shot — undo is safe while it loops.
        // WaterfallGiant (폭포 거인) enters a stunned "fake death" at 0 HP (HP→∞)
        // and sits in `die_loop` indefinitely while still alive in cs.Creatures.
        // Without this guard the "die"/"death" substring below matches that loop
        // forever, so AnyCreatureMidTransient stays true and undo is permanently
        // blocked (Z key dead). Reported as issue #3 (2026-06-16). Only genuine
        // one-shot transients (attack/cast/hurt/die one-shot, no _loop) gate undo.
        if (IsLoopShaped(name)) return false;
        foreach (var s in TransientPatterns)
            if (name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    /// <summary>
    /// Walk all live creatures and refresh IdleAnimCache for any whose current
    /// track 0 is a stable loop. Called at "settled" points (turn start) where
    /// the visual is reliable, so the cache stays fresh between snapshots.
    /// </summary>
    public static void RefreshIdleCacheFromLiveCreatures()
    {
        try
        {
            var cm = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
            if (cm == null) return;
            var cs = ReflectionCache.GetCombatState(cm);
            if (cs == null) return;

            int changed = 0;
            foreach (var c in cs.Creatures)
            {
                if (c == null) continue;
                var room = NCombatRoom.Instance;
                var nc = room?.GetCreatureNode(c);
                if (nc == null) continue;
                var observed = TryReadSpineAnim(nc);
                // Cache-update path: only true idle loops are eligible for
                // the fallback. Incapacitated loops poison the cache.
                if (observed != null && IsTrueIdleLoop(observed))
                {
                    if (!IdleAnimCache.TryGetValue(c, out var existing) || existing != observed)
                    {
                        IdleAnimCache[c] = observed;
                        changed++;
                    }
                }
            }
            if (changed > 0)
                RestoreLogger.Info($"[Snapshot] IdleAnimCache changed for {changed} creature(s)");
        }
        catch (Exception ex) { RestoreLogger.Warn($"[Snapshot] cache refresh failed: {ex.Message}"); }
    }

    /// <summary>
    /// Walk NCreature subtree, find NCreatureVisuals, read its SpineAnimation.GetCurrentTrack(0).GetAnimation().Name.
    /// Returns null on any reflection failure — pose-restore is best-effort.
    /// </summary>
    private static string? TryReadSpineAnim(NCreature nc) => TryReadSpineAnim(nc, 0);

    private static string? TryReadSpineAnim(NCreature nc, int trackIndex)
    {
        try
        {
            var visualsType = ReflectionCache.NCreatureVisualsType;
            if (visualsType == null) return null;
            object? visuals = null;
            foreach (var n in WalkChildren(nc))
            {
                if (visualsType.IsInstanceOfType(n)) { visuals = n; break; }
            }
            if (visuals == null) return null;

            var spine = ReflectionCache.NCVSpineAnimationProp?.GetValue(visuals);
            if (spine == null) return null;

            var track = ReflectionCache.SpineGetCurrentTrackMethod?.Invoke(spine, new object[] { trackIndex });
            if (track == null) return null;

            var anim = ReflectionCache.TrackGetAnimationMethod?.Invoke(track, null);
            if (anim == null) return null;

            // MegaAnimation exposes GetName() method (not a Name property).
            var getName = AccessTools.Method(anim.GetType(), "GetName");
            if (getName?.Invoke(anim, null) is string s && !string.IsNullOrEmpty(s)) return s;
            return null;
        }
        catch (Exception ex) { RestoreLogger.Warn($"[Snapshot] spine anim read failed: {ex.Message}"); return null; }
    }

    private static IEnumerable<Godot.Node> WalkChildren(Godot.Node root)
    {
        var stack = new Stack<Godot.Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            foreach (var c in n.GetChildren()) stack.Push(c);
        }
    }

    /// <summary>
    /// Walk the monster's class hierarchy (concrete subtype → MonsterModel base)
    /// and snapshot every private bool/int/enum/byte/short/long instance field.
    /// Floats/doubles included too — cheap. Reference fields skipped (we'd have
    /// to deep-clone). Static and constant fields skipped.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, (string key, FieldInfo field)[]> _monsterFieldCache = new();

    private static Dictionary<string, object?>? CaptureMonsterFields(MonsterModel monster)
    {
        var dict = new Dictionary<string, object?>();
        try
        {
            var entries = _monsterFieldCache.GetOrAdd(monster.GetType(), BuildMonsterFields);
            foreach (var (key, f) in entries)
            {
                try { dict[key] = f.GetValue(monster); }
                catch { }
            }
        }
        catch (Exception ex) { RestoreLogger.Warn($"[Snapshot] CaptureMonsterFields: {ex.Message}"); }
        return dict.Count > 0 ? dict : null;
    }

    private static (string key, FieldInfo field)[] BuildMonsterFields(Type type)
    {
        var list = new List<(string, FieldInfo)>();
        for (var t = type; t != null && t != typeof(object) && t != typeof(MonsterModel); t = t.BaseType)
        {
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic
                                          | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (f.IsLiteral || f.IsInitOnly) continue;
                var ft = f.FieldType;
                if (!ft.IsPrimitive && !ft.IsEnum) continue;
                list.Add(((t.FullName ?? t.Name) + "::" + f.Name, f));
            }
        }
        return list.ToArray();
    }

    private static MonsterMoveSnapshot? CaptureMonsterMove(MonsterModel monster)
    {
        var sm = monster.MoveStateMachine;
        if (sm == null) return null;

        var stateLog = new List<string>();
        if (ReflectionCache.SmStateLogProp?.GetValue(sm) is System.Collections.IEnumerable log)
        {
            foreach (var state in log)
            {
                var id = TryGetStateId(state);
                if (id != null) stateLog.Add(id);
            }
        }

        var performed = new Dictionary<string, bool>();
        if (ReflectionCache.SmStatesProp?.GetValue(sm) is System.Collections.IDictionary states
            && ReflectionCache.MoveStatePerformedField != null
            && ReflectionCache.MonsterStateType != null)
        {
            foreach (System.Collections.DictionaryEntry e in states)
            {
                if (e.Key is string key && e.Value != null)
                {
                    try
                    {
                        var v = ReflectionCache.MoveStatePerformedField.GetValue(e.Value);
                        if (v is bool b) performed[key] = b;
                    }
                    catch { }
                }
            }
        }

        return new MonsterMoveSnapshot
        {
            PerformedFirstMove = (bool)(ReflectionCache.SmPerformedFirstMoveField?.GetValue(sm) ?? false),
            SpawnedThisTurn = (bool)(ReflectionCache.MonsterSpawnedField?.GetValue(monster) ?? false),
            CurrentStateId = TryGetStateId(ReflectionCache.SmCurrentStateField?.GetValue(sm)),
            NextMoveStateId = TryGetStateId(monster.NextMove),
            StateLogIds = stateLog,
            MovePerformedAtLeastOnce = performed,
            // Strong refs to the actual MonsterState objects. Dynamically-
            // created states (e.g. CreatureCmd.Stun's "STUNNED" MoveState) are
            // not present in MoveStateMachine.States dict — without these
            // refs, restore would silently lose them and the slug wouldn't
            // come back stunned after a turn-cross undo.
            CurrentStateRef = ReflectionCache.SmCurrentStateField?.GetValue(sm),
            NextMoveRef = monster.NextMove,
        };
    }

    private static string? TryGetStateId(object? state)
    {
        if (state == null || ReflectionCache.MonsterStateIdProperty == null) return null;
        try { return ReflectionCache.MonsterStateIdProperty.GetValue(state) as string; }
        catch { return null; }
    }

    private static void CaptureRunRng(CombatSnapshot snap, RunState runState)
    {
        var rngSet = runState.Rng;
        if (rngSet == null) return;
        if (ReflectionCache.RunRngDictField.GetValue(rngSet)
            is not Dictionary<RunRngType, Rng> dict) return;

        foreach (var kv in dict)
            snap.RunRngs[kv.Key] = kv.Value.ToSerializable();
    }

    private static void CaptureHistory(CombatSnapshot snap, CombatManager cm)
    {
        var history = ReflectionCache.CmHistoryProperty?.GetValue(cm);
        if (history == null) return;
        if (ReflectionCache.HistoryEntriesField?.GetValue(history)
            is not System.Collections.IList entries) return;

        snap.HistoryEntries = new List<object>(entries.Count);
        foreach (var e in entries)
            if (e != null) snap.HistoryEntries.Add(e);
    }

    private static void CaptureSyncState(CombatSnapshot snap)
    {
        try
        {
            var syncr = RunManager.Instance?.ActionQueueSynchronizer;
            if (syncr != null) snap.SyncCombatState = syncr.CombatState;
            snap.CombatManagerPaused = CombatManager.Instance?.IsPaused ?? false;
        }
        catch { }
    }

    private static void CaptureSceneNodes(CombatSnapshot snap)
    {
        var room = MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom.Instance;
        if (room == null) return;
        foreach (var n in EnumerateTree(room)) snap.SceneNodes.Add(n);
    }

    internal static IEnumerable<Godot.Node> EnumerateTree(Godot.Node root)
    {
        var stack = new Stack<Godot.Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            foreach (var c in n.GetChildren()) stack.Push(c);
        }
    }

    /// <summary>
    /// Dumps every instance field of obj's runtime type (walking inheritance chain),
    /// deep-cloning the values. Used for relic subtype state — reference impl misses
    /// per-subtype private fields, leading to bugs like Stone Sword stack persistence.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, FieldInfo[]> _dumpFieldCache = new();

    private static Dictionary<FieldInfo, object?> DumpAllInstanceFields(object obj)
    {
        var dump = new Dictionary<FieldInfo, object?>();
        foreach (var f in _dumpFieldCache.GetOrAdd(obj.GetType(), BuildDumpFields))
        {
            object? value;
            try { value = f.GetValue(obj); } catch { continue; }
            dump[f] = DeepCloner.CloneObject(value);
        }
        return dump;
    }

    private static FieldInfo[] BuildDumpFields(Type type)
    {
        var list = new List<FieldInfo>();
        for (var t = type; t != null && t != typeof(object); t = t.BaseType)
        {
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic
                                          | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (f.IsLiteral || f.IsInitOnly) continue;
                if (f.Name is "_canonicalInstance" or "_owner") continue;
                if (typeof(Delegate).IsAssignableFrom(f.FieldType)) continue;
                list.Add(f);
            }
        }
        return list.ToArray();
    }
}

internal struct CreatureSnapshot
{
    public Creature Ref;
    public uint CombatId;
    public int CurrentHp;
    public int MaxHp;
    public int Block;
    public bool IsDead;
    public List<PowerSnapshot> Powers;

    public bool HadVisualNode;
    public Vector2 VisualPosition;
    public Vector2 VisualBodyScale;
    public Vector2 VisualBodyPosition;
    public float VisualBodyRotation;
    public Godot.Color VisualBodyModulate = Godot.Colors.White;
    /// <summary>Spine animation name on track 0 at snapshot time — re-applied
    /// on restore so the creature's pose returns to its idle state.</summary>
    public string? SpineAnimNameTrack0;
    /// <summary>NCreatureVisuals._hue — drives the death-tint liquid-overlay
    /// shader. Death anim ramps this to 1.0; without restore, kill+undo leaves
    /// the body shader-hidden even with transforms intact.</summary>
    public float? Hue;
    public double? LiquidOverlayTimer;
    /// <summary>The body's "true" normal material at snapshot time — what the
    /// SpineBody should render with. If a liquid overlay is currently active,
    /// this is `_savedNormalMaterial` (the pre-overlay base); otherwise it's
    /// the body's current `GetNormalMaterial()`. On restore we force-set the
    /// body to this so a stuck overlay shader can't survive the undo.</summary>
    public Godot.Material? BodyNormalMaterial;
    /// <summary>True if `_currentLiquidOverlayMaterial` was non-null at snapshot.
    /// On restore we always wipe overlay state to a clean baseline; this flag
    /// is informational only (logged for diagnostics).</summary>
    public bool LiquidOverlayWasActive;

    /// <summary>Strong ref to the body Node2D + its parent at snapshot time.
    /// If the death anim Free's the body (zombie has no body subtree) but
    /// the saved reference is still IsInstanceValid (only QueueFree-pending,
    /// or never freed), we can re-attach the body to NCreatureVisuals on
    /// revive. Without this, a late-Z undo (after die anim finished) leaves
    /// the creature with HP bar / intent / hover but no body sprite.</summary>
    public Godot.Node? BodyRef;
    public Godot.Node? BodyParentRef;

    public SerializableRng? MonsterRng;
    public MonsterMoveSnapshot? MonsterMove;
    /// <summary>Per-subtype private bool/int/enum field values captured from
    /// the concrete monster class (e.g. CorpseSlug._isRavenous). Restored on
    /// revive so creature-specific transient state doesn't leak across undo.
    /// Key = `Type.FullName::FieldName`.</summary>
    public Dictionary<string, object?>? MonsterFields;

    public CreatureSnapshot() { Powers = new(); }
}

internal struct PowerSnapshot
{
    public ModelId Id;
    public int Amount;
    public int AmountOnTurnStart;
    public bool SkipNextDurationTick;
    /// <summary>Deep clone (NOT MemberwiseClone) — Statue/Letter Opener fix.</summary>
    public object? InternalDataClone;
    /// <summary>Strong ref to the live PowerModel at snapshot time. Used by
    /// revive-power restore: when the creature died, the game stripped its
    /// `_powers` list — the model still exists (we hold a ref) but isn't
    /// attached. On revive we reattach the same instance (preserving any
    /// hook subscriptions) instead of dropping the powers entirely.</summary>
    public PowerModel? Ref;
    /// <summary>Result of the game's own PowerModel.MutableClone() — preserves
    /// every subtype private field (SurroundedPower._facing, ReattachPower
    /// counters, etc.) that the targeted Amount/_internalData capture misses.
    /// Restore copies fields back onto the live instance, skipping identity
    /// (`_owner`/`_canonicalInstance`/Id) and `_internalData` (the latter has
    /// its own DeepCloneFields path that re-INITs rather than preserves
    /// state — the InternalDataClone field above is the truth source).</summary>
    public PowerModel? Clone;
}

internal struct MonsterMoveSnapshot
{
    public string? CurrentStateId;
    public string? NextMoveStateId;
    /// <summary>Strong ref to the MoveStateMachine._currentState at capture
    /// time. Used when CurrentStateId isn't in the live States dict (i.e.
    /// dynamic MoveStates like Stun's "STUNNED").</summary>
    public object? CurrentStateRef;
    /// <summary>Strong ref to monster.NextMove at capture time. Same purpose
    /// as CurrentStateRef.</summary>
    public object? NextMoveRef;
    public bool PerformedFirstMove;
    public bool SpawnedThisTurn;
    /// <summary>State IDs of past moves, in order — needed for monsters whose
    /// next-move logic depends on the recent history (e.g. attack patterns).</summary>
    public List<string> StateLogIds;
    /// <summary>Per-state PerformedAtLeastOnce flag — needed for "perform once
    /// before transitioning" gates (e.g. boss reattach moves).</summary>
    public Dictionary<string, bool> MovePerformedAtLeastOnce;
}

internal struct RelicSnapshot
{
    public RelicModel Ref;
    public ModelId Id;
    public int StackCount;
    public object? Status;
    public object? DynamicVarsClone;
    /// <summary>Result of the game's own RelicModel.MutableClone() — preserves
    /// every mutable field via the subclass DeepCloneFields chain (Stone Sword's
    /// _internalData, Letter Opener's tracker, etc.). Restore copies fields back
    /// onto the live instance.</summary>
    public RelicModel? Clone;
}
