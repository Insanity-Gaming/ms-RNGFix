using InsanityGaming.RngFix.Models;

namespace InsanityGaming.RngFix.Services;

public interface IPlayerStateService
{
    ModulePlayerState GetOrCreate(int entityIndex);
    ModulePlayerState? Get(int entityIndex);
    void Reset(int entityIndex);
    void Remove(int entityIndex);

    /// <summary>
    /// Drops all tracked player state. Must be called on map change — state is keyed by pawn
    /// entity index, which the next map's entities reuse, so a stale entry would otherwise be
    /// silently inherited by an unrelated player (e.g. a leaked TouchingTeleportTriggerCount
    /// permanently disabling EdgeBugFix/InclineFix for that slot).
    /// </summary>
    void Clear();
}
