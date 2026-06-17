using Sharp.Shared.Enums;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;

namespace InsanityGaming.RngFix.Config;

public sealed class RngFixConVars
{
    // Plugin ConVars
    private readonly IConVar _cvDownhill;
    private readonly IConVar _cvUphill;
    private readonly IConVar _cvEdge;
    private readonly IConVar _cvTriggerJump;
    private readonly IConVar _cvTelehop;
    private readonly IConVar _cvStairs;
    private readonly IConVar _cvOldSlopeFix;
    private readonly IConVar _cvTouchTracking;

    // Engine ConVars
    private readonly IConVar? _cvMaxVelocity;
    private readonly IConVar? _cvGravity;
    private readonly IConVar? _cvAirAccelerate;
    private readonly IConVar? _cvTimeBetweenDucks;
    private readonly IConVar? _cvJumpImpulse;
    private readonly IConVar? _cvAutoBunnyHopping;

    public RngFixConVars(IConVarManager conVarManager)
    {
        _cvDownhill    = conVarManager.CreateConVar("rngfix_downhill",           1, "Enable downhill incline fix (0-1)",           ConVarFlags.Notify) ?? throw new InvalidOperationException("Failed to create rngfix_downhill");
        _cvUphill      = conVarManager.CreateConVar("rngfix_uphill",             1, "Uphill incline fix mode (-1/0/1)",             ConVarFlags.Notify) ?? throw new InvalidOperationException("Failed to create rngfix_uphill");
        _cvEdge        = conVarManager.CreateConVar("rngfix_edge",               1, "Enable edge bug fix (0-1)",                    ConVarFlags.Notify) ?? throw new InvalidOperationException("Failed to create rngfix_edge");
        _cvTriggerJump = conVarManager.CreateConVar("rngfix_triggerjump",        1, "Enable trigger jump fix (0-1)",                ConVarFlags.Notify) ?? throw new InvalidOperationException("Failed to create rngfix_triggerjump");
        _cvTelehop     = conVarManager.CreateConVar("rngfix_telehop",            1, "Enable telehop fix (0-1)",                    ConVarFlags.Notify) ?? throw new InvalidOperationException("Failed to create rngfix_telehop");
        _cvStairs      = conVarManager.CreateConVar("rngfix_stairs",             1, "Enable stair slide fix (0-1)",                ConVarFlags.Notify) ?? throw new InvalidOperationException("Failed to create rngfix_stairs");
        _cvOldSlopeFix    = conVarManager.CreateConVar("rngfix_useoldslopefixlogic", 0, "Use old slope fix logic for compat (0-1)", ConVarFlags.Notify) ?? throw new InvalidOperationException("Failed to create rngfix_useoldslopefixlogic");
        _cvTouchTracking  = conVarManager.CreateConVar("rngfix_touch_tracking",      1, "Enable manual trigger touch tracking (0-1)", ConVarFlags.Notify) ?? throw new InvalidOperationException("Failed to create rngfix_touch_tracking");

        _cvMaxVelocity = conVarManager.FindConVar("sv_maxvelocity");
        _cvGravity = conVarManager.FindConVar("sv_gravity");
        _cvAirAccelerate = conVarManager.FindConVar("sv_airaccelerate");
        _cvTimeBetweenDucks = conVarManager.FindConVar("sv_timebetweenducks");
        _cvJumpImpulse = conVarManager.FindConVar("sv_jump_impulse");
        _cvAutoBunnyHopping = conVarManager.FindConVar("sv_autobunnyhopping");
    }

    // Plugin ConVar accessors
    public bool IsDownhillEnabled => _cvDownhill.GetBool();
    public int UphillMode => _cvUphill.GetInt32();
    public bool IsEdgeEnabled => _cvEdge.GetBool();
    public bool IsTriggerJumpEnabled => _cvTriggerJump.GetBool();
    public bool IsTelehopEnabled => _cvTelehop.GetBool();
    public bool IsStairsEnabled => _cvStairs.GetBool();
    public bool UseOldSlopeFixLogic => _cvOldSlopeFix.GetBool();
    public bool IsTouchTrackingEnabled => _cvTouchTracking.GetBool();

    // Engine ConVar accessors
    public float MaxVelocity => _cvMaxVelocity?.GetFloat() ?? 3500f;
    public float Gravity => _cvGravity?.GetFloat() ?? 800f;
    public float AirAccelerate => _cvAirAccelerate?.GetFloat() ?? 10f;
    public float? TimeBetweenDucks => _cvTimeBetweenDucks?.GetFloat();
    public float? JumpImpulse => _cvJumpImpulse?.GetFloat();
    public bool AutoBunnyHopping => _cvAutoBunnyHopping?.GetBool() ?? false;

    // Aggregate
    public bool AnyPreTickFixEnabled => IsDownhillEnabled || UphillMode != 0 || IsEdgeEnabled || IsStairsEnabled || IsTelehopEnabled;
}
