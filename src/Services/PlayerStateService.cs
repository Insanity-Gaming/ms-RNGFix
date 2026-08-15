using InsanityGaming.RngFix.Models;

namespace InsanityGaming.RngFix.Services;

/// <summary>
/// Array-backed per-entity-index player state store. Entity indices are dense and bounded
/// (see <see cref="PhysicsConstants.MaxEntityIndex"/>), so a flat array avoids dictionary
/// hashing on the hottest path in the plugin (called for every player, every tick).
/// </summary>
public sealed class PlayerStateService : IPlayerStateService
{
    private readonly ModulePlayerState?[] _states = new ModulePlayerState?[PhysicsConstants.MaxEntityIndex];

    public ModulePlayerState GetOrCreate(int entityIndex)
    {
        if (!IsInRange(entityIndex))
            return new ModulePlayerState(); // Detached instance — never stored, never reused.

        var state = _states[entityIndex];
        if (state is null)
        {
            state = new ModulePlayerState();
            _states[entityIndex] = state;
        }

        return state;
    }

    public ModulePlayerState? Get(int entityIndex)
        => IsInRange(entityIndex) ? _states[entityIndex] : null;

    public void Reset(int entityIndex)
    {
        if (IsInRange(entityIndex))
            _states[entityIndex]?.Reset();
    }

    public void Remove(int entityIndex)
    {
        if (IsInRange(entityIndex))
            _states[entityIndex] = null;
    }

    public void Clear()
    {
        Array.Clear(_states, 0, _states.Length);
    }

    private static bool IsInRange(int entityIndex)
        => entityIndex > 0 && entityIndex < PhysicsConstants.MaxEntityIndex;
}
