using Sharp.Shared.GameEntities;

namespace InsanityGaming.RngFix.Services;

public interface ITriggerTouchSynthesizer
{
    void EnsureTouchAfterManualTrigger(IBaseEntity triggerEntity, IPlayerPawn pawn);
}
