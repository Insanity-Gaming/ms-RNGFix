using Sharp.Shared.Enums;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;

namespace InsanityGaming.RngFix.Config;

/// <summary>
/// Wraps all rngfix_* and consumed engine ConVars. Values are cached in fields and refreshed
/// via <see cref="IConVarManager.InstallChangeHook"/> rather than read from native storage on
/// every access — several of these accessors are called per player, per tick.
/// </summary>
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

    // Cached values — seeded from native storage once, then kept fresh via change hooks below.
    private bool _isDownhillEnabled;
    private int  _uphillMode;
    private bool _isEdgeEnabled;
    private bool _isTriggerJumpEnabled;
    private bool _isTelehopEnabled;
    private bool _isStairsEnabled;
    private bool _useOldSlopeFixLogic;
    private bool _isTouchTrackingEnabled;

    private float  _maxVelocity   = 3500f;
    private float  _gravity       = 800f;
    private float  _airAccelerate = 10f;
    private float? _timeBetweenDucks;
    private float? _jumpImpulse;
    private bool   _autoBunnyHopping;

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

        // Seed the cache, then wire change hooks so the fields track live edits without
        // requiring a native read on every access.
        _isDownhillEnabled      = _cvDownhill.GetBool();
        _uphillMode             = _cvUphill.GetInt32();
        _isEdgeEnabled          = _cvEdge.GetBool();
        _isTriggerJumpEnabled   = _cvTriggerJump.GetBool();
        _isTelehopEnabled       = _cvTelehop.GetBool();
        _isStairsEnabled        = _cvStairs.GetBool();
        _useOldSlopeFixLogic    = _cvOldSlopeFix.GetBool();
        _isTouchTrackingEnabled = _cvTouchTracking.GetBool();

        conVarManager.InstallChangeHook(_cvDownhill,      cv => _isDownhillEnabled      = cv.GetBool());
        conVarManager.InstallChangeHook(_cvUphill,        cv => _uphillMode             = cv.GetInt32());
        conVarManager.InstallChangeHook(_cvEdge,          cv => _isEdgeEnabled          = cv.GetBool());
        conVarManager.InstallChangeHook(_cvTriggerJump,   cv => _isTriggerJumpEnabled   = cv.GetBool());
        conVarManager.InstallChangeHook(_cvTelehop,       cv => _isTelehopEnabled       = cv.GetBool());
        conVarManager.InstallChangeHook(_cvStairs,        cv => _isStairsEnabled        = cv.GetBool());
        conVarManager.InstallChangeHook(_cvOldSlopeFix,   cv => _useOldSlopeFixLogic    = cv.GetBool());
        conVarManager.InstallChangeHook(_cvTouchTracking, cv => _isTouchTrackingEnabled = cv.GetBool());

        if (_cvMaxVelocity is not null)
        {
            _maxVelocity = _cvMaxVelocity.GetFloat();
            conVarManager.InstallChangeHook(_cvMaxVelocity, cv => _maxVelocity = cv.GetFloat());
        }

        if (_cvGravity is not null)
        {
            _gravity = _cvGravity.GetFloat();
            conVarManager.InstallChangeHook(_cvGravity, cv => _gravity = cv.GetFloat());
        }

        if (_cvAirAccelerate is not null)
        {
            _airAccelerate = _cvAirAccelerate.GetFloat();
            conVarManager.InstallChangeHook(_cvAirAccelerate, cv => _airAccelerate = cv.GetFloat());
        }

        if (_cvTimeBetweenDucks is not null)
        {
            _timeBetweenDucks = _cvTimeBetweenDucks.GetFloat();
            conVarManager.InstallChangeHook(_cvTimeBetweenDucks, cv => _timeBetweenDucks = cv.GetFloat());
        }

        if (_cvJumpImpulse is not null)
        {
            _jumpImpulse = _cvJumpImpulse.GetFloat();
            conVarManager.InstallChangeHook(_cvJumpImpulse, cv => _jumpImpulse = cv.GetFloat());
        }

        if (_cvAutoBunnyHopping is not null)
        {
            _autoBunnyHopping = _cvAutoBunnyHopping.GetBool();
            conVarManager.InstallChangeHook(_cvAutoBunnyHopping, cv => _autoBunnyHopping = cv.GetBool());
        }
    }

    // Plugin ConVar accessors — cached, refreshed via change hook.
    public bool IsDownhillEnabled => _isDownhillEnabled;
    public int UphillMode => _uphillMode;
    public bool IsEdgeEnabled => _isEdgeEnabled;
    public bool IsTriggerJumpEnabled => _isTriggerJumpEnabled;
    public bool IsTelehopEnabled => _isTelehopEnabled;
    public bool IsStairsEnabled => _isStairsEnabled;
    public bool UseOldSlopeFixLogic => _useOldSlopeFixLogic;
    public bool IsTouchTrackingEnabled => _isTouchTrackingEnabled;

    // Engine ConVar accessors — cached, refreshed via change hook.
    public float MaxVelocity => _maxVelocity;
    public float Gravity => _gravity;
    public float AirAccelerate => _airAccelerate;
    public float? TimeBetweenDucks => _timeBetweenDucks;
    public float? JumpImpulse => _jumpImpulse;
    public bool AutoBunnyHopping => _autoBunnyHopping;

    // Aggregate
    public bool AnyPreTickFixEnabled => _isDownhillEnabled || _uphillMode != 0 || _isEdgeEnabled || _isStairsEnabled || _isTelehopEnabled;
}
