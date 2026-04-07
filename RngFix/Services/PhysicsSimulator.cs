using InsanityGaming.RngFix.Config;
using InsanityGaming.RngFix.Models;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Managers;
using Sharp.Shared.Types;

namespace InsanityGaming.RngFix.Services;

/// <summary>
/// Replicates a subset of CGameMovement physics math in managed code.
/// All methods are stateless transforms — they read from PlayerState/IPlayerPawn and modify
/// the passed-in velocity/origin values only. No side effects on game state.
/// </summary>
public sealed class PhysicsSimulator : IPhysicsSimulator
{
    private readonly RngFixConVars _conVars;
    private readonly IPhysicsQueryManager _physicsQuery;

    // Precomputed hull vectors (CS2 values from PhysicsConstants)
    private static readonly Vector HullMins        = new(PhysicsConstants.HullMinX, PhysicsConstants.HullMinY, PhysicsConstants.HullMinZ);
    private static readonly Vector HullMaxsUnducked = new(PhysicsConstants.HullMaxX, PhysicsConstants.HullMaxY, PhysicsConstants.HullMaxZUnducked);
    private static readonly Vector HullMaxsDucked   = new(PhysicsConstants.HullMaxX, PhysicsConstants.HullMaxY, PhysicsConstants.HullMaxZDucked);

    // CS2 equivalent of MASK_PLAYERSOLID
    private static readonly InteractionLayers PlayerSolidLayers =
        InteractionLayers.Solid      | InteractionLayers.Sky        | InteractionLayers.PlayerClip |
        InteractionLayers.WorldGeometry | InteractionLayers.Slime   | InteractionLayers.Player    |
        InteractionLayers.PhysicsProp;

    public PhysicsSimulator(RngFixConVars conVars, IPhysicsQueryManager physicsQuery)
    {
        _conVars      = conVars;
        _physicsQuery = physicsQuery;
    }

    /// <inheritdoc/>
    public void SimulateDuck(ModulePlayerState state, IPlayerPawn pawn, ref Vector nextOrigin, out Vector mins, out Vector maxs)
    {
        EntityFlags flags = pawn.Flags;
        bool ducking     = flags.HasFlag(EntityFlags.Ducking);
        bool nextDucking = ducking;

        if (state.Buttons.HasFlag(UserCommandButtons.Duck) && !ducking)
        {
            // Wants to duck and currently standing — try to transition to ducked.
            if (!IsDuckCoolingDown(pawn))
            {
                nextOrigin   = new Vector(nextOrigin.X, nextOrigin.Y, nextOrigin.Z + PhysicsConstants.DuckDelta);
                nextDucking  = true;
            }
        }
        else if (!state.Buttons.HasFlag(UserCommandButtons.Duck) && ducking)
        {
            // Wants to unduck — check if there is room to stand.
            var triedOrigin = new Vector(nextOrigin.X, nextOrigin.Y, nextOrigin.Z - PhysicsConstants.DuckDelta);
            var hull  = new TraceShapeHull { Mins = HullMins, Maxs = HullMaxsUnducked };
            var trace = _physicsQuery.TraceShapeNoPlayers(
                new TraceShapeRay(hull), triedOrigin, triedOrigin,
                PlayerSolidLayers, CollisionGroupType.Default, TraceQueryFlag.All);

            if (!trace.DidHit())
            {
                nextOrigin  = triedOrigin;
                nextDucking = false;
            }
            // else: stuck — cannot unduck, leave origin unchanged
        }

        mins = HullMins;
        maxs = nextDucking ? HullMaxsDucked : HullMaxsUnducked;
    }

    /// <inheritdoc/>
    /// Mirrors CGameMovement::StartGravity. Applies first half-tick of gravity and adds
    /// the Z component of base velocity. Does NOT clear base velocity (this is a prediction).
    public void StartGravity(ModulePlayerState state, IPlayerPawn pawn, ref Vector velocity)
    {
        float localGravity = pawn.GravityScale;
        if (localGravity == 0f) localGravity = 1f;

        var baseVelocity = pawn.GetAbsVelocity();

        velocity = new Vector(
            velocity.X,
            velocity.Y,
            velocity.Z
                - localGravity * _conVars.Gravity * 0.5f * state.FrameTime
                + baseVelocity.Z * state.FrameTime);

        CheckVelocity(ref velocity);
    }

    /// <inheritdoc/>
    /// Mirrors CGameMovement::FinishGravity. Applies second half-tick of gravity.
    public void FinishGravity(ModulePlayerState state, IPlayerPawn pawn, ref Vector velocity)
    {
        float localGravity = pawn.GravityScale;
        if (localGravity == 0f) localGravity = 1f;

        velocity = new Vector(
            velocity.X,
            velocity.Y,
            velocity.Z - localGravity * _conVars.Gravity * 0.5f * state.FrameTime);

        CheckVelocity(ref velocity);
    }

    /// <inheritdoc/>
    /// Mirrors CGameMovement::CheckJumpButton. Only applies if player is on the ground and
    /// meets the jump input conditions. Calls FinishGravity internally (engine does this too,
    /// which is why jumping gives an extra half-tick of gravity — intentionally preserved here).
    public void CheckJumpButton(ModulePlayerState state, IPlayerPawn pawn, ref Vector velocity)
    {
        EntityFlags flags = pawn.Flags;
        if (!flags.HasFlag(EntityFlags.OnGround)) return;
        if (!CanJump(state)) return;

        float impulse   = GetJumpImpulse();
        bool  isDucking = (pawn.GetPlayerMovementService()?.KeyButtons.HasFlag(UserCommandButtons.Duck) ?? false)
                          || flags.HasFlag(EntityFlags.Ducking);

        // When ducking, velocity Z is set (not added) — this is the engine's source of the
        // extra height when jumping while crouched, which the original plugin deliberately preserves.
        if (isDucking)
            velocity = new Vector(velocity.X, velocity.Y, impulse);
        else
            velocity = new Vector(velocity.X, velocity.Y, velocity.Z + impulse);

        FinishGravity(state, pawn, ref velocity);
    }

    /// <inheritdoc/>
    /// Mirrors the relevant parts of CGameMovement::AirMove + CGameMovement::AirAccelerate.
    /// Computes the wish direction from view angles and input, then accelerates toward it.
    public void AirAccelerate(ModulePlayerState state, ref Vector velocity, float maxSpeed)
    {
        // Derive horizontal forward/right unit vectors from yaw only.
        // Pitch contribution to XY is zeroed (mirroring the engine's fore[2]=0 / NormalizeVector).
        // Roll is assumed to be 0 (standard play).
        float yawRad = state.AngleYaw * (MathF.PI / 180f);
        float sy = MathF.Sin(yawRad);
        float cy = MathF.Cos(yawRad);

        // fore = (cy, sy, 0) — already unit length after zeroing Z
        // side = (sy, -cy, 0) — already unit length
        float wishVelX = cy * state.ForwardMove + sy * state.SideMove;
        float wishVelY = sy * state.ForwardMove + (-cy) * state.SideMove;

        float wishSpeed = MathF.Sqrt(wishVelX * wishVelX + wishVelY * wishVelY);
        if (wishSpeed == 0f) return;

        float wishDirX = wishVelX / wishSpeed;
        float wishDirY = wishVelY / wishSpeed;

        if (maxSpeed != 0f && wishSpeed > maxSpeed) wishSpeed = maxSpeed;

        float wishSpd = wishSpeed > PhysicsConstants.AirSpeedCap ? PhysicsConstants.AirSpeedCap : wishSpeed;

        float currentSpeed = velocity.X * wishDirX + velocity.Y * wishDirY;
        float addSpeed     = wishSpd - currentSpeed;
        if (addSpeed <= 0f) return;

        float accelSpeed = _conVars.AirAccelerate * wishSpeed * state.FrameTime;
        if (accelSpeed > addSpeed) accelSpeed = addSpeed;

        velocity = new Vector(
            velocity.X + accelSpeed * wishDirX,
            velocity.Y + accelSpeed * wishDirY,
            velocity.Z);
    }

    /// <inheritdoc/>
    public void CheckVelocity(ref Vector velocity)
    {
        float max = _conVars.MaxVelocity;
        velocity = new Vector(
            Math.Clamp(velocity.X, -max, max),
            Math.Clamp(velocity.Y, -max, max),
            Math.Clamp(velocity.Z, -max, max));
    }

    /// <inheritdoc/>
    /// Mirrors CGameMovement::ClipVelocity (no overbounce — only valid for walkable surfaces).
    public void ClipVelocity(in Vector velocity, in Vector normal, out Vector result)
    {
        float backoff = velocity.X * normal.X + velocity.Y * normal.Y + velocity.Z * normal.Z;
        result = new Vector(
            velocity.X - normal.X * backoff,
            velocity.Y - normal.Y * backoff,
            velocity.Z - normal.Z * backoff);
    }

    /// <inheritdoc/>
    public float GetJumpImpulse() => _conVars.JumpImpulse ?? PhysicsConstants.DefaultJumpImpulse;

    /// <inheritdoc/>
    public bool IsInWater(IPlayerPawn pawn) => pawn.Flags.HasFlag(EntityFlags.WaterJump);

    // ──────────────────────────────── Private helpers ────────────────────────────────

    /// <summary>
    /// Returns true if the player is allowed to initiate a jump this tick.
    /// Mirrors CGameMovement::CheckJumpButton's input conditions.
    /// </summary>
    private bool CanJump(ModulePlayerState state)
    {
        if (!state.Buttons.HasFlag(UserCommandButtons.Jump)) return false;

        // KeyChangedButtons holds bits that changed this tick (XOR semantics).
        // If IN_JUMP is NOT in ChangedButtons, jump was already held last tick — block unless autobhop.
        bool jumpNewThisTick = state.ChangedButtons.HasFlag(UserCommandButtons.Jump);
        if (!jumpNewThisTick && !_conVars.AutoBunnyHopping) return false;

        return true;
    }

    /// <summary>
    /// Returns true if the duck transition is on cooldown.
    /// Mirrors CGameMovement::CanUnduck / the duck speed check.
    /// </summary>
    private static bool IsDuckCoolingDown(IPlayerPawn pawn)
    {
        // TODO: The time-based cooldown (sv_timebetweenducks vs m_flLastDuckTime) requires a
        // GetGameTime() equivalent from ISharedSystem/IModSharp which is not yet confirmed.
        // The duck speed check alone covers the primary guard used in CS2.
        float duckSpeed = pawn.GetPlayerMovementService()?.DuckSpeed ?? 0f;
        return duckSpeed < PhysicsConstants.DuckMinDuckSpeed;
    }
}
