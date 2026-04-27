using Sharp.Shared.GameEntities;

namespace InsanityGaming.RngFix.Services;

public interface ITriggerTouchSynthesizer
{
    void EnsureTouchAfterManualTrigger(IBaseEntity triggerEntity, IPlayerPawn pawn);
    void CleanupSyntheticTouch(IBaseEntity triggerEntity, IPlayerPawn pawn);
    void CleanupPlayer(int playerIdx);
    void CleanupTrigger(int triggerIdx);
}
