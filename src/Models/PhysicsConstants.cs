using Sharp.Shared.Enums;

namespace InsanityGaming.RngFix.Models;

public static class PhysicsConstants
{
    // CS2 MASK_PLAYERSOLID equivalent — single definition, edit here to affect all traces
    public static readonly InteractionLayers PlayerSolidLayers =
        InteractionLayers.Solid         /*| InteractionLayers.Sky  */       | InteractionLayers.PlayerClip |
        InteractionLayers.WorldGeometry | InteractionLayers.Slime       | InteractionLayers.Player     |
        InteractionLayers.PhysicsProp;

    // Engine physics constants (do NOT change — match CS2 engine values)
    public const float LandHeight = 2.0f;
    public const float NonJumpVelocity = 140.0f;
    public const float MinStandableZNrm = 0.7f;
    public const float AirSpeedCap = 30.0f;
    public const float DuckMinDuckSpeed = 1.5f;
    public const float DefaultJumpImpulse = 301.99337741f;

    // CS2 player hull dimensions
    public const float HullMinX = -16.0f;
    public const float HullMinY = -16.0f;
    public const float HullMinZ = 0.0f;
    public const float HullMaxX = 16.0f;
    public const float HullMaxY = 16.0f;
    public const float HullMaxZUnducked = 72.0f;
    public const float HullMaxZDucked = 64.0f;
    public const float DuckDelta = (HullMaxZUnducked - HullMaxZDucked) / 2f; // 4.0f

    // Entity flags (CS2 values)
    public const int FL_ONGROUND = 1 << 0;
    public const int FL_DUCKING = 1 << 1;
    public const int FL_BASEVELOCITY = 1 << 9;

    // Move types
    public const int MoveTypeWalk = 2;

    // Input button flags (CS2 64-bit buttons)
    public const ulong IN_JUMP = 2UL;
    public const ulong IN_DUCK = 4UL;

    // NetVar string keys (cached as constants to avoid string allocation in hot path)
    // public const string NetVarBaseVelocity = "m_vecBaseVelocity";
    // public const string NetVarLaggedMovementValue = "m_flLaggedMovementValue";
    // public const string NetVarGroundEntity = "m_hGroundEntity";
    // public const string NetVarDucking = "m_bDucking";
    // public const string NetVarFlags = "m_fFlags";
    // public const string NetVarMoveType = "m_MoveType";
    // public const string NetVarGravity = "m_flGravity";
    public const string NetVarWaterLevel = "m_flWaterLevel";
    // public const string NetVarLastDuckTime = "m_flLastDuckTime";
    // public const string NetVarDuckSpeed = "m_flDuckSpeed";
    // public const string NetVarStepSize = "m_flStepSize";
    // public const string NetVarAbsVelocity = "m_vecAbsVelocity"; // (Get/Set)AbsVelocity
    // public const string NetVarVelocity = "m_vecVelocity"; // (Get/Set)LocalVelocity
    // public const string NetVarMoveParent = "m_hMoveParent";
    // public const string NetVarAbsOrigin  = "m_vecAbsOrigin";
    // public const string NetVarMins       = "m_vecMins";
    // public const string NetVarMaxs       = "m_vecMaxs";

    // Uphill mode enum values
    public const int UphillLoss = -1;
    public const int UphillDefault = 0;
    public const int UphillNeutral = 1;
}
