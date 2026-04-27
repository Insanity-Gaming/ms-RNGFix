using Sharp.Shared.GameEntities;
using Sharp.Shared.Managers;

namespace InsanityGaming.RngFix.Services;

public sealed unsafe class TriggerTouchSynthesizer : ITriggerTouchSynthesizer
{
    private readonly IEntityManager _entityManager;
    private readonly ITriggerTracker _triggerTracker;
    private readonly TriggerNatives _triggerNatives;
    private readonly HashSet<(int PlayerIdx, int TriggerIdx)> _syntheticTouches = new();

    public TriggerTouchSynthesizer(IEntityManager entityManager, ITriggerTracker triggerTracker, TriggerNatives triggerNatives)
    {
        _entityManager = entityManager;
        _triggerTracker = triggerTracker;
        _triggerNatives = triggerNatives;
    }

    public void EnsureTouchAfterManualTrigger(IBaseEntity triggerEntity, IPlayerPawn pawn)
    {
        var trigger = triggerEntity.As<IBaseTrigger>();
        if (!trigger.IsValid())
            return;

        int playerIdx = pawn.Index;
        int triggerIdx = trigger.Index;

        if (_triggerTracker.IsTouching(playerIdx, triggerIdx))
            return;

        _triggerNatives.Touch(triggerEntity, pawn);

        if (!TriggerTouchingContains(trigger, playerIdx))
            AddSyntheticTouch(trigger, pawn);

        _triggerTracker.SetTouching(playerIdx, triggerIdx, true);
    }

    public void CleanupSyntheticTouch(IBaseEntity triggerEntity, IPlayerPawn pawn)
    {
        var trigger = triggerEntity.As<IBaseTrigger>();
        if (!trigger.IsValid())
            return;

        CleanupSyntheticTouch(trigger, pawn.Index);
    }

    public void CleanupPlayer(int playerIdx)
    {
        var touchesToCleanup = _syntheticTouches
            .Where(entry => entry.PlayerIdx == playerIdx)
            .ToArray();

        foreach (var entry in touchesToCleanup)
        {
            IBaseEntity? triggerEntity;

            try
            {
                triggerEntity = _entityManager.FindEntityByIndex(entry.TriggerIdx);
            }
            catch
            {
                triggerEntity = null;
            }

            if (triggerEntity?.As<IBaseTrigger>() is { } trigger && trigger.IsValid())
                RemoveSyntheticTouch(trigger, playerIdx);

            _syntheticTouches.Remove(entry);
        }
    }

    public void CleanupTrigger(int triggerIdx)
    {
        _syntheticTouches.RemoveWhere(entry => entry.TriggerIdx == triggerIdx);
    }

    private void CleanupSyntheticTouch(IBaseTrigger trigger, int playerIdx)
    {
        if (!_syntheticTouches.Remove((playerIdx, trigger.Index)))
            return;

        RemoveSyntheticTouch(trigger, playerIdx);
    }

    private bool TriggerTouchingContains(IBaseTrigger trigger, int playerIdx)
    {
        var touching = trigger.GetTouchingEntities().GetUtlVector();

        for (int i = 0; i < touching->Count; i++)
        {
            var handle = touching->Element(i);
            if (handle.GetEntryIndex() == playerIdx)
                return true;
        }

        return false;
    }

    private void AddSyntheticTouch(IBaseTrigger trigger, IPlayerPawn pawn)
    {
        var touching = trigger.GetTouchingEntities().GetUtlVector();

        if (TriggerTouchingContains(trigger, pawn.Index))
            return;

        touching->Add(pawn.Handle);
        _syntheticTouches.Add((pawn.Index, trigger.Index));
    }

    private void RemoveSyntheticTouch(IBaseTrigger trigger, int playerIdx)
    {
        var touching = trigger.GetTouchingEntities().GetUtlVector();

        for (int i = 0; i < touching->Count; i++)
        {
            var handle = touching->Element(i);
            if (handle.GetEntryIndex() != playerIdx)
                continue;

            touching->Remove(i);
            break;
        }
    }
}
