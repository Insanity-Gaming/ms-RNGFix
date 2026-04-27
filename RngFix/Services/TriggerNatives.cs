using Sharp.Shared;
using Sharp.Shared.GameEntities;

namespace InsanityGaming.RngFix.Services;

/// <summary>
/// Wraps native virtual function calls on trigger entities that are not exposed
/// through the managed ModSharp API.
/// </summary>
public sealed unsafe class TriggerNatives
{
    private readonly int _passesTriggerFiltersIndex;
    private readonly int _touchIndex;

    public TriggerNatives(ISharedSystem sharedSystem)
    {
        var gameData = sharedSystem.GetModSharp().GetGameData();
        _passesTriggerFiltersIndex = gameData.GetVFuncIndex("CBaseTrigger::PassesTriggerFilters");
        _touchIndex                = gameData.GetVFuncIndex("CBaseEntity::Touch");
    }

    /// <summary>
    /// Calls CBaseTrigger::PassesTriggerFilters via vtable dispatch.
    /// Returns true if the trigger would fire for the given entity (respects filter
    /// entities, team checks, and spawnflags).
    /// </summary>
    public bool PassesTriggerFilters(IBaseEntity trigger, IBaseEntity other)
    {
        if (!trigger.IsValid() || !other.IsValid())
            return false;

        var triggerPtr = trigger.GetAbsPtr();
        var otherPtr   = other.GetAbsPtr();

        if (triggerPtr == nint.Zero || otherPtr == nint.Zero || _passesTriggerFiltersIndex < 0)
            return false;

        var vtable = *(nint**)triggerPtr;
        if (vtable == null)
            return false;

        var fn = (delegate* unmanaged<nint, nint, bool>)vtable[_passesTriggerFiltersIndex];

        return fn(triggerPtr, otherPtr);
    }
    
    /// <summary>
    /// Calls CBaseEntity::Touch (continuous touch update)
    /// </summary>
    public void Touch(IBaseEntity trigger, IBaseEntity other)
    {
        if (!trigger.IsValid() || !other.IsValid())
            return;

        var ptr = trigger.GetAbsPtr();
        if (ptr == nint.Zero || _touchIndex <= 0)
            return;

        var vtable = *(nint**)ptr;
        if (vtable == null)
            return;

        var fn = (delegate* unmanaged<nint, nint, void>)vtable[_touchIndex];

        fn(ptr, other.GetAbsPtr());
    }
}
