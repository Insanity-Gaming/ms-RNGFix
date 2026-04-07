using InsanityGaming.RngFix.Models;

namespace InsanityGaming.RngFix.Services;

public sealed class PlayerStateService : IPlayerStateService
{
    private readonly Dictionary<int, ModulePlayerState> _states = new();

    public ModulePlayerState GetOrCreate(int entityIndex)
    {
        if (!_states.TryGetValue(entityIndex, out var state))
        {
            state = new ModulePlayerState();
            _states[entityIndex] = state;
        }

        return state;
    }

    public ModulePlayerState? Get(int entityIndex)
    {
        _states.TryGetValue(entityIndex, out var state);
        return state;
    }

    public void Reset(int entityIndex)
    {
        if (_states.TryGetValue(entityIndex, out var state))
        {
            state.Reset();
        }
    }

    public void Remove(int entityIndex)
    {
        _states.Remove(entityIndex);
    }
}
