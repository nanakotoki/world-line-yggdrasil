using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Orbs;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Orbs;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;
using System.Reflection;

namespace WorldLineYggdrasil.Restore;

/// <summary>
/// Static reflection cache. Field/method lookups happen once at type initialization
/// so capture+restore hot paths are reflection-free.
///
/// Any null entry means the game has renamed/moved that member — the snapshot will
/// be incomplete but won't crash. We log all NULL entries on startup so a game
/// update is easy to diagnose.
/// </summary>
internal static class ReflectionCache
{
    // CombatManager
    public static readonly MethodInfo? CombatManagerGetStateMethod =
        AccessTools.Method(typeof(CombatManager), "DebugOnlyGetState");

    /// <summary>
    /// v0.111.0 holds the combat state as <c>CombatManager._turnState.State</c>;
    /// <c>DebugOnlyGetState()</c> is the supported accessor.
    /// </summary>
    public static CombatState? GetCombatState(CombatManager cm)
        => CombatManagerGetStateMethod?.Invoke(cm, null) as CombatState;

    // CombatState
    public static readonly FieldInfo CsAlliesField =
        AccessTools.Field(typeof(CombatState), "_allies");
    public static readonly FieldInfo CsEnemiesField =
        AccessTools.Field(typeof(CombatState), "_enemies");
    public static readonly FieldInfo? AllCardsField =
        AccessTools.Field(typeof(CombatState), "_allCards");
    public static readonly FieldInfo? NextCreatureIdField =
        AccessTools.Field(typeof(CombatState), "_nextCreatureId");

    // Creature
    public static readonly FieldInfo CreatureHpField =
        AccessTools.Field(typeof(Creature), "_currentHp");
    public static readonly FieldInfo CreatureMaxHpField =
        AccessTools.Field(typeof(Creature), "_maxHp");
    public static readonly FieldInfo CreatureBlockField =
        AccessTools.Field(typeof(Creature), "_block");
    public static readonly FieldInfo CreaturePowersField =
        AccessTools.Field(typeof(Creature), "_powers");

    // Creature events — backing fields for auto-events. We fire these manually
    // after restoring HP/Block via reflection so visuals (HP bars, block icons)
    // refresh to the rolled-back values. Without this, the model rolls back but
    // the visual stays at the post-action state.
    public static readonly FieldInfo? CreatureCurrentHpChangedField =
        AccessTools.Field(typeof(Creature), "CurrentHpChanged");
    public static readonly FieldInfo? CreatureMaxHpChangedField =
        AccessTools.Field(typeof(Creature), "MaxHpChanged");
    public static readonly FieldInfo? CreatureBlockChangedField =
        AccessTools.Field(typeof(Creature), "BlockChanged");
    public static readonly FieldInfo? CreatureRevivedField =
        AccessTools.Field(typeof(Creature), "Revived");

    // PlayerCombatState
    public static readonly FieldInfo PcsEnergyField =
        AccessTools.Field(typeof(PlayerCombatState), "_energy");
    public static readonly FieldInfo PcsStarsField =
        AccessTools.Field(typeof(PlayerCombatState), "_stars");
    /// <summary>Private setter for <c>TurnNumber</c> (auto-property backing setter).</summary>
    public static readonly MethodInfo? PcsTurnNumberSetter =
        AccessTools.PropertySetter(typeof(PlayerCombatState), "TurnNumber");
    public static readonly FieldInfo? PcsPetsField =
        AccessTools.Field(typeof(PlayerCombatState), "_pets");
    public static readonly FieldInfo? PcsPilesField =
        AccessTools.Field(typeof(PlayerCombatState), "_piles");

    // CardPile
    public static readonly FieldInfo CardPileCardsField =
        AccessTools.Field(typeof(CardPile), "_cards");

    // PowerModel
    public static readonly FieldInfo PowerAmountField =
        AccessTools.Field(typeof(PowerModel), "_amount");
    public static readonly FieldInfo PowerAmountOnTurnStartField =
        AccessTools.Field(typeof(PowerModel), "_amountOnTurnStart");
    public static readonly FieldInfo PowerSkipField =
        AccessTools.Field(typeof(PowerModel), "_skipNextDurationTick");
    public static readonly FieldInfo? PowerInternalDataField =
        AccessTools.Field(typeof(PowerModel), "_internalData");

    // MonsterModel + move state machine
    public static readonly FieldInfo? MonsterRngField =
        AccessTools.Field(typeof(MonsterModel), "_rng");
    public static readonly FieldInfo? MonsterSpawnedField =
        AccessTools.Field(typeof(MonsterModel), "_spawnedThisTurn");
    public static readonly FieldInfo? MonsterMoveStateMachineField =
        AccessTools.Field(typeof(MonsterModel), "_moveStateMachine");
    public static readonly PropertyInfo? NextMoveProp =
        AccessTools.Property(typeof(MonsterModel), "NextMove");

    public static readonly Type? MonsterMoveStateMachineType =
        AccessTools.TypeByName(
            "MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MonsterMoveStateMachine");
    public static readonly Type? MonsterStateType =
        AccessTools.TypeByName(
            "MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine.MonsterState");
    public static readonly FieldInfo? SmCurrentStateField =
        MonsterMoveStateMachineType != null
            ? AccessTools.Field(MonsterMoveStateMachineType, "_currentState") : null;
    public static readonly FieldInfo? SmPerformedFirstMoveField =
        MonsterMoveStateMachineType != null
            ? AccessTools.Field(MonsterMoveStateMachineType, "_performedFirstMove") : null;
    public static readonly PropertyInfo? MonsterStateIdProperty =
        MonsterStateType != null
            ? AccessTools.Property(MonsterStateType, "Id") : null;

    public static readonly PropertyInfo? SmStatesProp =
        MonsterMoveStateMachineType != null
            ? AccessTools.Property(MonsterMoveStateMachineType, "States") : null;
    public static readonly PropertyInfo? SmStateLogProp =
        MonsterMoveStateMachineType != null
            ? AccessTools.Property(MonsterMoveStateMachineType, "StateLog") : null;
    public static readonly MethodInfo? SmForceCurrentStateMethod =
        MonsterMoveStateMachineType != null
            ? AccessTools.Method(MonsterMoveStateMachineType, "ForceCurrentState") : null;
    public static readonly FieldInfo? MoveStatePerformedField =
        MonsterStateType != null
            ? AccessTools.Field(MonsterStateType, "_performedAtLeastOnce") : null;

    // RelicModel
    public static readonly FieldInfo? RelicDynamicVarsField =
        AccessTools.Field(typeof(RelicModel), "_dynamicVars");
    public static readonly FieldInfo? RelicStackCountField =
        AccessTools.Field(typeof(RelicModel), "<StackCount>k__BackingField");
    public static readonly PropertyInfo? RelicStatusProperty =
        AccessTools.Property(typeof(RelicModel), "Status");

    // Player
    public static readonly FieldInfo PlayerPotionSlotsField =
        AccessTools.Field(typeof(Player), "_potionSlots");
    public static readonly FieldInfo? PlayerGoldField =
        AccessTools.Field(typeof(Player), "_gold");

    // PotionModel
    public static readonly FieldInfo? PotionRemovedField =
        AccessTools.Field(typeof(PotionModel), "<HasBeenRemovedFromState>k__BackingField");
    public static readonly FieldInfo? PotionOwnerField =
        AccessTools.Field(typeof(PotionModel), "_owner");

    // RNG
    public static readonly FieldInfo RunRngDictField =
        AccessTools.Field(typeof(RunRngSet), "_rngs");
    public static readonly PropertyInfo? RunManagerStateProperty =
        AccessTools.Property(typeof(RunManager), "State");

    // CombatHistory (typed via property to avoid TypeByName)
    public static readonly PropertyInfo? CmHistoryProperty =
        AccessTools.Property(typeof(CombatManager), "History");
    public static readonly FieldInfo? HistoryEntriesField =
        CmHistoryProperty?.PropertyType != null
            ? AccessTools.Field(CmHistoryProperty.PropertyType, "_entries") : null;

    // PowerModel restoration helpers
    public static readonly MethodInfo? PowerInvokeAmountChangedMethod =
        AccessTools.Method(typeof(PowerModel), "InvokeAmountChanged");
    /// <summary>PowerModel._owner. The public Owner setter throws if the field
    /// is non-null and value differs ("Cannot move power from one owner to
    /// another"). On creature death the game strips powers and clears _owner;
    /// on revive we reattach the same PowerModel ref via direct private write.</summary>
    public static readonly FieldInfo? PowerOwnerField =
        AccessTools.Field(typeof(PowerModel), "_owner");

    /// <summary>PowerModel.ActivateHooks / DeactivateHooks — register/unregister
    /// the power's Hook subscriptions (BeforeCardPlayed, EnergyCost modifiers,
    /// etc.). Called by the game when a power is applied / removed from a
    /// creature. Our restore path manipulates `_powers` list directly via
    /// reflection, bypassing the official Apply/Remove pipeline — without
    /// also calling these, hook subscriptions get out of sync with list
    /// membership. Concrete bug: Pounce → FreeSkillPower applied → Undo →
    /// FreeSkillPower removed from list but its BeforeCardPlayed hook still
    /// fires, making the next skill 0-cost when it shouldn't be (or vice
    /// versa: power restored to list but hook not re-subscribed, so cost
    /// modifier doesn't fire). Discovered by walking the PowerModel
    /// inheritance chain — the methods may live on a HookSubscriber base.</summary>
    public static readonly MethodInfo? PowerActivateHooksMethod =
        FindMethodOnPowerModelChain("ActivateHooks");
    public static readonly MethodInfo? PowerDeactivateHooksMethod =
        FindMethodOnPowerModelChain("DeactivateHooks");

    private static MethodInfo? FindMethodOnPowerModelChain(string name)
    {
        for (var t = typeof(PowerModel); t != null && t != typeof(object); t = t.BaseType)
        {
            var m = AccessTools.Method(t, name);
            if (m != null) return m;
        }
        return null;
    }

    // Card back-reference fixups (post-MutableClone restore). Without these,
    // global cost modifiers (e.g. VoidForm) and CalculatedVars (e.g. exhaust-count
    // damage multipliers) read state from the clone instead of the live card —
    // power cards in particular fail to register as playable.
    // CardEnergyCost lives in MegaCrit.Sts2.Core.Entities.Cards (NOT
    // .Core.Models as it might appear from convention). Verified directly
    // against sts2.dll metadata 2026-04-29. Until this was fixed, the
    // resolved Type was null, EnergyCostCardField was null, and the
    // FixCardBackReferences EnergyCost path silently no-op'd — leaving
    // every restored card's _energyCost._card pointing at the clone
    // instead of live, which broke cost-modifier round-tripping (Pounce
    // / Unrelenting / Havoc 0-cost effects, VoidForm-style power cost
    // modifiers, etc.).
    public static readonly Type? CardEnergyCostType =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.Entities.Cards.CardEnergyCost");
    public static readonly FieldInfo? EnergyCostCardField =
        CardEnergyCostType != null ? AccessTools.Field(CardEnergyCostType, "_card") : null;

    // CardEnergyCost._localModifiers — list of per-card LocalCostModifier
    // entries used by Pounce/Unrelenting/Havoc to apply "next attack/skill
    // costs 0" effects via card.EnergyCost.AddUntilPlayed(0) etc. Lives on
    // CardEnergyCost (which sits on card._energyCost), NOT on CardModel
    // directly. CardEnergyCost.Clone() creates a fresh _localModifiers list,
    // so a snapshot's clone has its own list — but only if our restore
    // correctly copies the cloned CardEnergyCost back onto the live card.
    // Diagnostic logging reads this field to verify the modifier list
    // round-trips through undo.
    public static readonly FieldInfo? CardEnergyCostLocalModifiersField =
        CardEnergyCostType != null
            ? AccessTools.Field(CardEnergyCostType, "_localModifiers")
            : null;
    public static readonly PropertyInfo? CardEnergyCostProp =
        AccessTools.Property(typeof(CardModel), "EnergyCost");
    public static readonly PropertyInfo? CardDynamicVarsProp =
        AccessTools.Property(typeof(CardModel), "DynamicVars");
    public static readonly MethodInfo? DynamicVarsInitializeWithOwnerMethod =
        CardDynamicVarsProp?.PropertyType != null
            ? AccessTools.Method(CardDynamicVarsProp.PropertyType, "InitializeWithOwner")
            : null;

    // Enchantment / Affliction back-references. CardModel.DeepCloneFields() clones
    // both attached models and re-attaches them to the CLONE card via EnchantInternal /
    // AfflictInternal, so cloned Enchantment._card / Affliction._card point at the
    // clone (not live). Copying the card's <Enchantment>/<Affliction> backing fields
    // back onto live therefore leaves enchantment._card dangling at the clone, whose
    // CombatState is null — Imbued.BeforePlayPhaseStart hits NRE on `Card.Owner`,
    // faulting StartTurn so PlayPhase never inits and cards become unusable.
    public static readonly PropertyInfo? CardEnchantmentProp =
        AccessTools.Property(typeof(CardModel), "Enchantment");
    public static readonly PropertyInfo? CardAfflictionProp =
        AccessTools.Property(typeof(CardModel), "Affliction");
    public static readonly Type? EnchantmentModelType =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.Models.EnchantmentModel");
    public static readonly Type? AfflictionModelType =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.Models.AfflictionModel");
    public static readonly FieldInfo? EnchantmentCardField =
        EnchantmentModelType != null ? AccessTools.Field(EnchantmentModelType, "_card") : null;
    public static readonly FieldInfo? AfflictionCardField =
        AfflictionModelType != null ? AccessTools.Field(AfflictionModelType, "_card") : null;

    // Card mutable fields — same skip set as the reference impl, to avoid
    // overwriting identity / canonical / cloning bookkeeping.
    public static readonly FieldInfo[] CardMutableFields = InitCardMutableFields();
    private static FieldInfo[] InitCardMutableFields()
    {
        var skip = new HashSet<string>
        {
            "_cloneOf", "_canonicalInstance", "_deckVersion", "_owner",
            "_isDupe", "_currentTarget", "_isEnchantmentPreview",
            "<Id>k__BackingField", "<IsMutable>k__BackingField",
            "<CategorySortingId>k__BackingField", "<EntrySortingId>k__BackingField",
        };
        var list = new List<FieldInfo>();
        for (var t = typeof(CardModel); t != null && t != typeof(object); t = t.BaseType)
        {
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic
                                          | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (f.IsLiteral || f.IsInitOnly) continue;
                if (skip.Contains(f.Name)) continue;
                list.Add(f);
            }
        }
        return list.ToArray();
    }

    // OrbQueue — data layer (per-PlayerCombatState orb storage)
    public static readonly FieldInfo? OrbQueueOrbsField =
        AccessTools.Field(typeof(OrbQueue), "_orbs");
    public static readonly FieldInfo? OrbQueueCapacityField =
        AccessTools.Field(typeof(OrbQueue), "<Capacity>k__BackingField");

    // NOrbManager — visual layer (lives on NCreature.OrbManager).
    // Orb slot disappearance after undo happens because the live NOrb child
    // nodes get freed during the play action and we need to rebuild both the
    // filled slots (with OrbModel) AND the empty placeholder slots up to
    // OrbQueue.Capacity, otherwise the slot row collapses to zero.
    public static readonly FieldInfo? NOrbManagerOrbsField =
        AccessTools.Field(typeof(NOrbManager), "_orbs");
    public static readonly FieldInfo? NOrbManagerContainerField =
        AccessTools.Field(typeof(NOrbManager), "_orbContainer");
    public static readonly FieldInfo? NOrbManagerTweenField =
        AccessTools.Field(typeof(NOrbManager), "_curTween");
    public static readonly MethodInfo? NOrbManagerTweenLayoutMethod =
        AccessTools.Method(typeof(NOrbManager), "TweenLayout");
    public static readonly MethodInfo? NOrbManagerUpdateNavMethod =
        AccessTools.Method(typeof(NOrbManager), "UpdateControllerNavigation");

    // NCombatRoom — visual layer
    public static readonly FieldInfo? NcrRemovingNodesField =
        AccessTools.Field(typeof(NCombatRoom), "_removingCreatureNodes");
    public static readonly PropertyInfo? NCreatureEntityProp =
        AccessTools.Property(typeof(NCreature), "Entity");

    // Death-anim cancellation. NCreature runs death animation as an async Task
    // tracked via DeathAnimationTask + DeathAnimCancelToken. StartReviveAnim
    // does NOT cancel the in-flight task — so without explicit cancel, the
    // async AnimDie continues even after we've moved the zombie back to active
    // and forced the spine pose to idle. The task eventually overrides modulate
    // / hue / queue-frees the node again. Fix: cancel token + kill the visual
    // Tweens that AnimDie is driving before calling StartReviveAnim.
    public static readonly PropertyInfo? NCreatureDeathAnimCancelTokenProp =
        AccessTools.Property(typeof(NCreature), "DeathAnimCancelToken");
    public static readonly PropertyInfo? NCreatureIsPlayingDeathAnimProp =
        AccessTools.Property(typeof(NCreature), "IsPlayingDeathAnimation");
    public static readonly FieldInfo? NCreatureIntentFadeTweenField =
        AccessTools.Field(typeof(NCreature), "_intentFadeTween");
    public static readonly FieldInfo? NCreatureShakeTweenField =
        AccessTools.Field(typeof(NCreature), "_shakeTween");
    public static readonly FieldInfo? NCreatureScaleTweenField =
        AccessTools.Field(typeof(NCreature), "_scaleTween");

    // Spine animation handles — used to capture the creature's pose animation
    // at snapshot time and force-restore it on undo (otherwise hit reactions or
    // state-driven pose changes leave the visual stuck in a different pose).
    public static readonly Type? NCreatureVisualsType =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Combat.NCreatureVisuals");
    public static readonly PropertyInfo? NCVSpineAnimationProp =
        NCreatureVisualsType != null ? AccessTools.Property(NCreatureVisualsType, "SpineAnimation") : null;

    // Liquid-overlay shader hue. Death anim sets this toward 1.0 (full
    // dead-tint); the kill+undo bug had the body reused with hue=1 stuck,
    // making the sprite shader-blank even though all transforms were correct.
    // Capture+restore this and the timer that drives it.
    public static readonly FieldInfo? NCVHueField =
        NCreatureVisualsType != null ? AccessTools.Field(NCreatureVisualsType, "_hue") : null;
    public static readonly FieldInfo? NCVLiquidOverlayTimerField =
        NCreatureVisualsType != null ? AccessTools.Field(NCreatureVisualsType, "_liquidOverlayTimer") : null;
    // Material backing fields. Restoring just _liquidOverlayTimer leaves the
    // shader material on the body if our undo lands outside the live ApplyLiquidOverlayInternal
    // loop's clean-up window — the creature renders with the goopy potion overlay
    // permanently stuck on top.
    public static readonly FieldInfo? NCVSavedNormalMaterialField =
        NCreatureVisualsType != null ? AccessTools.Field(NCreatureVisualsType, "_savedNormalMaterial") : null;
    public static readonly FieldInfo? NCVCurrentLiquidOverlayMaterialField =
        NCreatureVisualsType != null ? AccessTools.Field(NCreatureVisualsType, "_currentLiquidOverlayMaterial") : null;
    public static readonly Type? SpineAnimAccessType =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.Bindings.MegaSpine.SpineAnimationAccess");
    public static readonly MethodInfo? SpineGetCurrentTrackMethod =
        SpineAnimAccessType != null ? AccessTools.Method(SpineAnimAccessType, "GetCurrentTrack") : null;
    public static readonly MethodInfo? SpineSetAnimationMethod =
        SpineAnimAccessType != null ? AccessTools.Method(SpineAnimAccessType, "SetAnimation") : null;
    public static readonly Type? MegaTrackEntryType =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.Bindings.MegaSpine.MegaTrackEntry");
    public static readonly MethodInfo? TrackGetAnimationMethod =
        MegaTrackEntryType != null ? AccessTools.Method(MegaTrackEntryType, "GetAnimation") : null;
    public static readonly Type? MegaAnimationType =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.Bindings.MegaSpine.MegaAnimation");
    public static readonly MethodInfo? AnimationGetDurationMethod =
        MegaAnimationType != null ? AccessTools.Method(MegaAnimationType, "GetDuration") : null;

    public static readonly Type? MegaSpriteType =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.Bindings.MegaSpine.MegaSprite");
    public static readonly MethodInfo? MegaSpriteGetSkeletonMethod =
        MegaSpriteType != null ? AccessTools.Method(MegaSpriteType, "GetSkeleton") : null;
    public static readonly MethodInfo? MegaSpriteGetNormalMaterialMethod =
        MegaSpriteType != null ? AccessTools.Method(MegaSpriteType, "GetNormalMaterial") : null;
    public static readonly MethodInfo? MegaSpriteSetNormalMaterialMethod =
        MegaSpriteType != null ? AccessTools.Method(MegaSpriteType, "SetNormalMaterial") : null;
    public static readonly Type? MegaSkeletonType =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.Bindings.MegaSpine.MegaSkeleton");
    public static readonly MethodInfo? SkeletonSetSlotsToSetupPoseMethod =
        MegaSkeletonType != null ? AccessTools.Method(MegaSkeletonType, "SetSlotsToSetupPose") : null;

    // Hurt-animation handles. The methods aren't on NCreature itself — they
    // live on a child/related type. We scan the sts2 assembly once at startup
    // to find whichever type owns them.
    public static readonly Type? HurtAnimType = DiscoverHurtAnimType();
    /// <summary>NCreatureVisuals.IsPlayingHurtAnimation is a *method*, not a
    /// property, in current game build (it asserts the spine track 0 anim
    /// name == "hurt"). Earlier code looked it up as a property and got
    /// NULL — leaving our hurt cancellation a no-op.</summary>
    public static readonly MethodInfo? HurtAnimIsPlayingMethod =
        HurtAnimType != null ? AccessTools.Method(HurtAnimType, "IsPlayingHurtAnimation") : null;
    /// <summary>Kept for back-compat callers that still ask for the property,
    /// but the live game has IsPlayingHurtAnimation as a method.</summary>
    public static readonly PropertyInfo? HurtAnimIsPlayingProp =
        HurtAnimType != null ? AccessTools.Property(HurtAnimType, "IsPlayingHurtAnimation") : null;
    public static readonly MethodInfo? HurtAnimSkipMethod =
        HurtAnimType != null ? AccessTools.Method(HurtAnimType, "SkipHurtAnim") : null;
    public static readonly MethodInfo? HurtAnimOnEndMethod =
        HurtAnimType != null ? AccessTools.Method(HurtAnimType, "OnHurtEnd") : null;

    private static Type? DiscoverHurtAnimType()
    {
        // Direct hit: previous scan showed Godot's source-gen MethodName nested
        // class on NCreatureVisuals carries IsPlayingHurtAnimation, which means
        // the parent class owns the actual method.
        var t = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Combat.NCreatureVisuals");
        if (t != null)
        {
            DumpHurtAnimApi(t);
            DumpAllMembers(t);

            // MegaSprite — the spine renderer.
            var megaSpriteType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Bindings.MegaSpine.MegaSprite");
            if (megaSpriteType != null)
            {
                DumpAllMembers(megaSpriteType);
                // What type does GetSkeleton return? Skin-related API likely lives there.
                var getSkel = megaSpriteType.GetMethod("GetSkeleton",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (getSkel != null)
                {
                    RestoreLogger.Info($"[Reflection] GetSkeleton returns: {getSkel.ReturnType.FullName}");
                    DumpAllMembers(getSkel.ReturnType);
                }
            }

            // SpineAnimation drives the pose — walk all assemblies to find its type.
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types?.Where(x => x != null).ToArray()! ?? Array.Empty<Type>(); }
                catch { continue; }
                foreach (var ty in types)
                {
                    if (ty?.Name == "SpineAnimationAccess")
                    {
                        DumpAllMembers(ty);
                        // The return type of GetCurrentTrack is the track-entry struct/class.
                        var m = ty.GetMethod("GetCurrentTrack",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (m != null)
                        {
                            RestoreLogger.Info($"[Reflection] GetCurrentTrack returns: {m.ReturnType.FullName}");
                            DumpAllMembers(m.ReturnType);
                        }
                        var sm = ty.GetMethod("SetAnimation",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (sm != null)
                        {
                            RestoreLogger.Info($"[Reflection] SetAnimation params: {string.Join(",", sm.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))}");
                        }
                        // GetAnimation on MegaTrackEntry returns... let's find that and dump too.
                        var trackType = m?.ReturnType;
                        var getAnim = trackType?.GetMethod("GetAnimation",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (getAnim != null)
                        {
                            var animType = getAnim.ReturnType;
                            RestoreLogger.Info($"[Reflection] GetAnimation returns: {animType.FullName}");
                            DumpAllMembers(animType);
                        }
                        goto found;
                    }
                }
            }
            found:;
        }
        return t;
    }

    private static void DumpAllMembers(Type t)
    {
        try
        {
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic
                                  | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            var lines = new List<string>();
            foreach (var f in t.GetFields(F)) lines.Add($"F:{f.Name}:{f.FieldType.Name}");
            foreach (var p in t.GetProperties(F)) lines.Add($"P:{p.Name}:{p.PropertyType.Name}");
            foreach (var m in t.GetMethods(F))
            {
                if (m.IsSpecialName) continue;
                lines.Add($"M:{m.Name}({m.GetParameters().Length})");
            }
            RestoreLogger.Info($"[Reflection] {t.Name} all members:");
            const int chunkSize = 8;
            for (int i = 0; i < lines.Count; i += chunkSize)
                RestoreLogger.Info($"  {string.Join(", ", lines.Skip(i).Take(chunkSize))}");
        }
        catch (Exception ex) { RestoreLogger.Warn($"[Reflection] DumpAllMembers failed: {ex.Message}"); }
    }

    private static void DumpHurtAnimApi(Type t)
    {
        try
        {
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic
                                  | BindingFlags.Instance | BindingFlags.Static;
            var hurts = new List<string>();
            var refreshes = new List<string>();
            foreach (var m in t.GetMethods(F))
            {
                if (m.Name.IndexOf("Hurt", StringComparison.OrdinalIgnoreCase) >= 0)
                    hurts.Add($"M:{m.Name}({m.GetParameters().Length})");
                if (m.Name.StartsWith("Refresh", StringComparison.Ordinal)
                    || m.Name.StartsWith("Update", StringComparison.Ordinal)
                    || m.Name.StartsWith("Rebuild", StringComparison.Ordinal)
                    || m.Name.StartsWith("Sync", StringComparison.Ordinal)
                    || m.Name.StartsWith("Render", StringComparison.Ordinal))
                    refreshes.Add($"M:{m.Name}({m.GetParameters().Length})");
            }
            foreach (var p in t.GetProperties(F))
                if (p.Name.IndexOf("Hurt", StringComparison.OrdinalIgnoreCase) >= 0)
                    hurts.Add($"P:{p.Name}");
            foreach (var f in t.GetFields(F))
                if (f.Name.IndexOf("Hurt", StringComparison.OrdinalIgnoreCase) >= 0)
                    hurts.Add($"F:{f.Name}");
            RestoreLogger.Info($"[Reflection] {t.Name} hurt-related members: {string.Join(", ", hurts)}");
            RestoreLogger.Info($"[Reflection] {t.Name} refresh/update methods: {string.Join(", ", refreshes)}");

            // Same dump for NCreature itself.
            var nctype = typeof(NCreature);
            var ncRefreshes = new List<string>();
            foreach (var m in nctype.GetMethods(F))
            {
                if (m.Name.StartsWith("Refresh", StringComparison.Ordinal)
                    || m.Name.StartsWith("Update", StringComparison.Ordinal)
                    || m.Name.StartsWith("Rebuild", StringComparison.Ordinal)
                    || m.Name.StartsWith("Sync", StringComparison.Ordinal)
                    || m.Name.StartsWith("Render", StringComparison.Ordinal))
                    ncRefreshes.Add($"M:{m.Name}({m.GetParameters().Length})");
            }
            RestoreLogger.Info($"[Reflection] NCreature refresh/update methods: {string.Join(", ", ncRefreshes)}");

            // What event-fire helpers does Creature expose? Same pattern as
            // PowerModel.InvokeAmountChanged — required so reflection-set fields
            // notify subscribed visuals.
            var creatureType = typeof(MegaCrit.Sts2.Core.Entities.Creatures.Creature);
            var invokers = new List<string>();
            foreach (var m in creatureType.GetMethods(F))
            {
                if (m.Name.StartsWith("Invoke", StringComparison.Ordinal)
                    || m.Name.StartsWith("Notify", StringComparison.Ordinal)
                    || m.Name.StartsWith("Fire", StringComparison.Ordinal)
                    || m.Name.StartsWith("On", StringComparison.Ordinal))
                    invokers.Add($"M:{m.Name}({m.GetParameters().Length})");
            }
            RestoreLogger.Info($"[Reflection] Creature invoke/notify methods: {string.Join(", ", invokers.Take(30))}");

            var events = new List<string>();
            foreach (var e in creatureType.GetEvents(F))
            {
                var handlerType = e.EventHandlerType;
                var invoke = handlerType?.GetMethod("Invoke");
                var sig = invoke != null
                    ? string.Join(",", invoke.GetParameters().Select(p => p.ParameterType.Name))
                    : "?";
                events.Add($"E:{e.Name}({sig})");
            }
            RestoreLogger.Info($"[Reflection] Creature events: {string.Join(", ", events)}");
        }
        catch (Exception ex) { RestoreLogger.Warn($"[Reflection] DumpHurtAnimApi failed: {ex.Message}"); }
    }

    // CombatStateTracker — UI refresh trigger
    public static readonly MethodInfo? NotifyCombatStateChangedMethod =
        AccessTools.Method(typeof(CombatStateTracker), "NotifyCombatStateChanged");

    // CombatManager end-turn / phase state
    public static readonly FieldInfo? CmPlayersReadyToEndTurnField =
        AccessTools.Field(typeof(CombatManager), "_playersReadyToEndTurn");
    public static readonly FieldInfo? CmPlayersReadyToBeginEnemyTurnField =
        AccessTools.Field(typeof(CombatManager), "_playersReadyToBeginEnemyTurn");
    public static readonly PropertyInfo? CmPlayerActionsDisabledProp =
        AccessTools.Property(typeof(CombatManager), "PlayerActionsDisabled");

    // NPlayerHand — internal state used by CanTurnBeEnded
    public static readonly FieldInfo? HandCurrentCardPlayField =
        AccessTools.Field(typeof(NPlayerHand), "_currentCardPlay");
    public static readonly FieldInfo? HandCurrentModeField =
        AccessTools.Field(typeof(NPlayerHand), "_currentMode");

    // NCreatureStateDisplay refresh — replays HP, block, nameplate, healthbar
    // foreground/middleground/text from current model state. Critical: without
    // calling this on every restore, the displayed HP/block can lag the
    // restored model values until the next event tick.
    public static readonly MethodInfo? NCreatureStateDisplayRefreshValuesMethod =
        AccessTools.Method(typeof(NCreatureStateDisplay), "RefreshValues");

    // AnimateOut tween references + the captured original position. AnimateOut
    // tweens Modulate.A → 0 then chains a TweenCallback that sets Visible=false;
    // it also tweens Position to (Position - _healthBarAnimOffset). On undo we
    // force-show (Visible=true, A=1f), but if these tweens are still in flight
    // they keep lerping toward 0 and the callback re-hides — TestSubject HP-bar
    // invisible bug (Y50J2MGVWX3, 2026-05-02). Kill both tweens and snap
    // Position back to _originalPosition before the force-show.
    public static readonly FieldInfo? NCreatureStateDisplayShowHideTweenField =
        AccessTools.Field(typeof(NCreatureStateDisplay), "_showHideTween");
    public static readonly FieldInfo? NCreatureStateDisplayHoverTweenField =
        AccessTools.Field(typeof(NCreatureStateDisplay), "_hoverTween");
    public static readonly FieldInfo? NCreatureStateDisplayOriginalPositionField =
        AccessTools.Field(typeof(NCreatureStateDisplay), "_originalPosition");

    // NPowerContainer rebuild
    public static readonly FieldInfo? NCreatureStateDisplayPowerContainerField =
        AccessTools.Field(typeof(NCreatureStateDisplay), "_powerContainer");
    public static readonly FieldInfo? NPowerContainerNodesField =
        AccessTools.Field(typeof(NPowerContainer), "_powerNodes");
    public static readonly MethodInfo? NPowerContainerAddMethod =
        AccessTools.Method(typeof(NPowerContainer), "Add", new[] { typeof(PowerModel) });
    /// <summary>NPowerContainer._creature back-ref. _ExitTree clears the
    /// PowerApplied/Removed handler bindings on this creature; if death
    /// detached the container subtree, on revive _EnterTree may run with
    /// _creature==null (the field is never re-assigned by _EnterTree). We
    /// rebind via reflection during PowerRefresher.</summary>
    public static readonly FieldInfo? NPowerContainerCreatureField =
        AccessTools.Field(typeof(NPowerContainer), "_creature");
    public static readonly MethodInfo? NPowerContainerConnectSignalsMethod =
        AccessTools.Method(typeof(NPowerContainer), "ConnectCreatureSignals");

    // NCombatCardPile (pile count buttons)
    public static readonly Type? NCombatCardPileType =
        AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Combat.NCombatCardPile");
    public static readonly FieldInfo? NCombatCardPileCurrentCountField =
        NCombatCardPileType != null ? AccessTools.Field(NCombatCardPileType, "_currentCount") : null;
    public static readonly FieldInfo? NCombatCardPileCountLabelField =
        NCombatCardPileType != null ? AccessTools.Field(NCombatCardPileType, "_countLabel") : null;

    static ReflectionCache()
    {
        LogStartupDiagnostics();
    }

    private static void LogStartupDiagnostics()
    {
        // List every (name, isnull) tuple — anything NULL means the next game update
        // broke a member name and we'll need to update this file.
        var nulls = new List<string>();
        void Check(string name, object? member) { if (member == null) nulls.Add(name); }

        Check(nameof(AllCardsField), AllCardsField);
        Check(nameof(NextCreatureIdField), NextCreatureIdField);
        Check(nameof(PcsPetsField), PcsPetsField);
        Check(nameof(PcsPilesField), PcsPilesField);
        Check(nameof(PowerInternalDataField), PowerInternalDataField);
        Check(nameof(PowerActivateHooksMethod), PowerActivateHooksMethod);
        Check(nameof(PowerDeactivateHooksMethod), PowerDeactivateHooksMethod);
        Check(nameof(MonsterRngField), MonsterRngField);
        Check(nameof(MonsterSpawnedField), MonsterSpawnedField);
        Check(nameof(MonsterMoveStateMachineField), MonsterMoveStateMachineField);
        Check(nameof(NextMoveProp), NextMoveProp);
        Check(nameof(MonsterMoveStateMachineType), MonsterMoveStateMachineType);
        Check(nameof(MonsterStateType), MonsterStateType);
        Check(nameof(SmCurrentStateField), SmCurrentStateField);
        Check(nameof(SmPerformedFirstMoveField), SmPerformedFirstMoveField);
        Check(nameof(MonsterStateIdProperty), MonsterStateIdProperty);
        Check(nameof(RelicDynamicVarsField), RelicDynamicVarsField);
        Check(nameof(RelicStackCountField), RelicStackCountField);
        Check(nameof(RelicStatusProperty), RelicStatusProperty);
        Check(nameof(PlayerGoldField), PlayerGoldField);
        Check(nameof(PotionRemovedField), PotionRemovedField);
        Check(nameof(PotionOwnerField), PotionOwnerField);
        Check(nameof(CmHistoryProperty), CmHistoryProperty);
        Check(nameof(HistoryEntriesField), HistoryEntriesField);
        Check(nameof(NcrRemovingNodesField), NcrRemovingNodesField);
        Check(nameof(NCreatureEntityProp), NCreatureEntityProp);
        Check(nameof(NCreatureDeathAnimCancelTokenProp), NCreatureDeathAnimCancelTokenProp);
        Check(nameof(NCreatureIsPlayingDeathAnimProp), NCreatureIsPlayingDeathAnimProp);
        Check(nameof(NCreatureIntentFadeTweenField), NCreatureIntentFadeTweenField);
        Check(nameof(NCreatureShakeTweenField), NCreatureShakeTweenField);
        Check(nameof(NCreatureScaleTweenField), NCreatureScaleTweenField);
        Check(nameof(NCVHueField), NCVHueField);
        Check(nameof(NCVLiquidOverlayTimerField), NCVLiquidOverlayTimerField);
        Check(nameof(OrbQueueOrbsField), OrbQueueOrbsField);
        Check(nameof(OrbQueueCapacityField), OrbQueueCapacityField);
        Check(nameof(NOrbManagerOrbsField), NOrbManagerOrbsField);
        Check(nameof(NOrbManagerContainerField), NOrbManagerContainerField);
        Check(nameof(NOrbManagerTweenField), NOrbManagerTweenField);
        Check(nameof(NOrbManagerTweenLayoutMethod), NOrbManagerTweenLayoutMethod);
        Check(nameof(NOrbManagerUpdateNavMethod), NOrbManagerUpdateNavMethod);
        Check(nameof(HurtAnimType), HurtAnimType);
        Check(nameof(HurtAnimIsPlayingProp), HurtAnimIsPlayingProp);
        Check(nameof(HurtAnimSkipMethod), HurtAnimSkipMethod);
        Check(nameof(HurtAnimOnEndMethod), HurtAnimOnEndMethod);
        if (HurtAnimType != null)
            RestoreLogger.Info($"[Reflection] HurtAnim type discovered: {HurtAnimType.FullName}");

        DumpNCombatRoomCreatureApi();
        DumpDeathRelatedApi();

        if (nulls.Count > 0)
            RestoreLogger.Warn($"[Reflection] {nulls.Count} member(s) NULL — game update may have changed: {string.Join(", ", nulls)}");
        else
            RestoreLogger.Info("[Reflection] all reflection targets resolved.");
    }

    /// <summary>
    /// One-time enumeration of NCombatRoom and NCreature methods/fields with names
    /// containing "creature", "spawn", "add", "remove", "revive", "recreate",
    /// "restore", "init", or "create". Used to discover the correct entry point
    /// for visual revive — current `room.AddCreature` returns a node but the
    /// sprite never renders, suggesting we're missing a follow-up call.
    /// </summary>
    private static void DumpNCombatRoomCreatureApi()
    {
        try
        {
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic
                                  | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            string[] needles = { "creature", "spawn", "add", "remove", "revive",
                                 "recreate", "restore", "init", "create", "build", "load", "ready" };
            bool Match(string name)
            {
                foreach (var s in needles)
                    if (name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                return false;
            }

            foreach (var t in new[] { typeof(NCombatRoom), typeof(NCreature) })
            {
                var lines = new List<string>();
                foreach (var f in t.GetFields(F))
                    if (Match(f.Name)) lines.Add($"F:{f.Name}:{f.FieldType.Name}");
                foreach (var p in t.GetProperties(F))
                    if (Match(p.Name)) lines.Add($"P:{p.Name}:{p.PropertyType.Name}");
                foreach (var m in t.GetMethods(F))
                {
                    if (m.IsSpecialName) continue;
                    if (!Match(m.Name)) continue;
                    var sig = string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name));
                    lines.Add($"M:{m.Name}({sig})");
                }
                RestoreLogger.Info($"[Reflection] {t.Name} creature/spawn API:");
                const int chunk = 6;
                for (int i = 0; i < lines.Count; i += chunk)
                    RestoreLogger.Info($"  {string.Join(", ", lines.Skip(i).Take(chunk))}");
            }

            // Full field dump of NCombatRoom — we suspect there's a dict/list
            // (Creature → NCreature) that caches the visual mapping and survives
            // our zombie cleanup. AddCreature reuses the cached shell instead of
            // instantiating a fresh prefab. We need to find and clear that cache.
            // Walk the inheritance chain since the cache lives in a base class.
            RestoreLogger.Info("[Reflection] NCombatRoom inheritance fields:");
            for (var ct = typeof(NCombatRoom); ct != null && ct != typeof(object); ct = ct.BaseType)
            {
                foreach (var f in ct.GetFields(F))
                    RestoreLogger.Info($"  [{ct.Name}] F:{f.Name} : {f.FieldType.FullName}");
            }

            // Full method signatures for the visual-rebuild candidates we
            // discovered in the previous dump. Param types matter — SetUpSkin(1)
            // could take Creature, MonsterModel, string skin id, or something else.
            RestoreLogger.Info("[Reflection] full sigs for revive candidates:");
            foreach (var (t, names) in new (Type t, string[] names)[]
            {
                (NCreatureVisualsType ?? typeof(object),
                    new[] { "SetUpSkin", "SetScaleAndHue", "GetCurrentBody", "_Ready", "_EnterTree", "_ExitTree" }),
                (typeof(NCombatRoom),
                    new[] { "CreateAllyNodes", "CreateEnemyNodes", "AddCreature", "AdjustCreatureScaleForAspectRatio", "PositionCreaturesWithSlots" }),
                (typeof(NCreature),
                    new[] { "_Ready", "_EnterTree", "_ExitTree", "AnimTempRevive", "StartReviveAnim", "ShowCreatureHoverTips" }),
            })
            {
                if (t == typeof(object)) continue;
                foreach (var name in names)
                {
                    foreach (var m in t.GetMethods(F).Where(m => m.Name == name))
                    {
                        var sig = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.FullName} {p.Name}"));
                        RestoreLogger.Info($"  [{t.Name}] M:{m.Name}({sig}) -> {m.ReturnType.Name}");
                    }
                }
            }
        }
        catch (Exception ex) { RestoreLogger.Warn($"[Reflection] DumpNCombatRoomCreatureApi failed: {ex.Message}"); }
    }

    /// <summary>
    /// Death-anim entry-point discovery. After in-place revive succeeds (zombie
    /// node moved back from _removingCreatureNodes), the death animation often
    /// keeps running and fades the creature back to invisible. The fade is
    /// likely driven by:
    ///   - a Godot Tween held in a field on NCreature/NCreatureVisuals (or a
    ///     base class), OR
    ///   - a Timer node child that fires QueueFree at the end of death anim, OR
    ///   - a spine "die" track still playing on track 0.
    /// Dump everything name-matching "die"/"death"/"dying"/"dead"/"tween"/
    /// "timer"/"fade"/"remove"/"queue" on NCreature + NCreatureVisuals
    /// (incl. inherited fields) so we can pick the exact thing to Kill/Cancel.
    /// </summary>
    private static void DumpDeathRelatedApi()
    {
        try
        {
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic
                                  | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            string[] needles = { "die", "death", "dying", "dead", "tween",
                                 "timer", "fade", "remove", "queue", "kill" };
            bool Match(string name)
            {
                foreach (var s in needles)
                    if (name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                return false;
            }

            var typesToDump = new List<Type>();
            for (var ct = typeof(NCreature); ct != null && ct != typeof(object); ct = ct.BaseType)
                typesToDump.Add(ct);
            if (NCreatureVisualsType != null)
                for (var ct = NCreatureVisualsType; ct != null && ct != typeof(object); ct = ct.BaseType)
                    typesToDump.Add(ct);

            foreach (var t in typesToDump)
            {
                var lines = new List<string>();
                foreach (var f in t.GetFields(F))
                    if (Match(f.Name)) lines.Add($"F:{f.Name}:{f.FieldType.Name}");
                foreach (var p in t.GetProperties(F))
                    if (Match(p.Name)) lines.Add($"P:{p.Name}:{p.PropertyType.Name}");
                foreach (var m in t.GetMethods(F))
                {
                    if (m.IsSpecialName) continue;
                    if (!Match(m.Name)) continue;
                    var sig = string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name));
                    lines.Add($"M:{m.Name}({sig})");
                }
                if (lines.Count == 0) continue;
                RestoreLogger.Info($"[Reflection] {t.Name} death-related members:");
                const int chunk = 6;
                for (int i = 0; i < lines.Count; i += chunk)
                    RestoreLogger.Info($"  {string.Join(", ", lines.Skip(i).Take(chunk))}");
            }

            // Also dump full-fields lists (non-filtered) for NCreature and
            // NCreatureVisuals — declared-only on each level. The death-anim
            // handle could live under an unobvious name (e.g. "_anim", "_t"),
            // so the full list is the safety net.
            RestoreLogger.Info("[Reflection] NCreature full field list:");
            for (var ct = typeof(NCreature); ct != null && ct != typeof(object); ct = ct.BaseType)
                foreach (var f in ct.GetFields(F))
                    RestoreLogger.Info($"  [{ct.Name}] F:{f.Name} : {f.FieldType.FullName}");
            if (NCreatureVisualsType != null)
            {
                RestoreLogger.Info("[Reflection] NCreatureVisuals full field list:");
                for (var ct = NCreatureVisualsType; ct != null && ct != typeof(object); ct = ct.BaseType)
                    foreach (var f in ct.GetFields(F))
                        RestoreLogger.Info($"  [{ct.Name}] F:{f.Name} : {f.FieldType.FullName}");
            }
        }
        catch (Exception ex) { RestoreLogger.Warn($"[Reflection] DumpDeathRelatedApi failed: {ex.Message}"); }
    }
}
