using InsanityGaming.RngFix.Models;

namespace InsanityGaming.RngFix.Services;

public interface IPlayerStateService
{
    ModulePlayerState GetOrCreate(int entityIndex);
    ModulePlayerState? Get(int entityIndex);
    void Reset(int entityIndex);
    void Remove(int entityIndex);
}
