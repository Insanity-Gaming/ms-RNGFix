using Sharp.Shared.GameEntities;

namespace InsanityGaming.RngFix.Services;

/// <summary>
/// Fires the engine's real CBaseEntity::Touch for a trigger a player's movement should have
/// touched but the engine's own collision pass skipped this tick.
///
/// This intentionally does NOT synthesize engine-side touch-list membership (no raw writes into
/// CBaseTrigger::m_hTouchingEntities). That approach previously required an unchecked
/// As&lt;IBaseTrigger&gt;() cast on entity indices resolved after map changes/disconnects, which — once
/// those indices went stale — wrote through a trigger-shaped schema offset into an entity that was
/// no longer a trigger. That is the confirmed source of the ~17h uptime crash. See
/// TryApply in TriggerJumpFix and the removed synthesizer history for context.
///
/// Trade-off accepted: a synthesized touch fires the trigger once via its own Touch() logic, but
/// is not tracked as an ongoing engine-side touch. Continuous/repeating triggers will not keep
/// re-firing from a synthesized entry, and the engine will not produce an OnEndTouch for it.
/// </summary>
public sealed class TriggerTouchSynthesizer : ITriggerTouchSynthesizer
{
    private readonly ITriggerTracker _triggerTracker;
    private readonly TriggerNatives _triggerNatives;

    public TriggerTouchSynthesizer(ITriggerTracker triggerTracker, TriggerNatives triggerNatives)
    {
        _triggerTracker = triggerTracker;
        _triggerNatives = triggerNatives;
    }

    /// <remarks>
    /// Callers must have already verified <paramref name="triggerEntity"/>'s classname starts with
    /// "trigger_" this same tick (see TriggerJumpFix.TryApply) — the As&lt;IBaseTrigger&gt;() cast below
    /// is unchecked and only safe against an entity resolved fresh, not a cached/stale index.
    /// </remarks>
    public void EnsureTouchAfterManualTrigger(IBaseEntity triggerEntity, IPlayerPawn pawn)
    {
        if (!triggerEntity.IsValid())
            return;

        string classname;
        try { classname = triggerEntity.Classname; }
        catch { return; }

        if (string.IsNullOrEmpty(classname) ||
            !classname.StartsWith("trigger_", StringComparison.OrdinalIgnoreCase))
            return;

        var trigger = triggerEntity.As<IBaseTrigger>();
        if (!trigger.IsValid())
            return;

        int playerIdx = pawn.Index;
        int triggerIdx = trigger.Index;

        if (_triggerTracker.IsTouching(playerIdx, triggerIdx))
            return;

        _triggerNatives.Touch(triggerEntity, pawn);
    }
}
