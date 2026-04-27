using InsanityGaming.RngFix.Config;
using InsanityGaming.RngFix.Models;
using InsanityGaming.RngFix.Services;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Types;

namespace InsanityGaming.RngFix.Fixes;

/// <summary>
/// Two-part fix for incline collisions:
///
/// Pre-tick (TryPreventUphill): When the player will hit an uphill surface this tick and the
/// engine would negatively affect their speed, rewind their origin to prevent the collision.
///
/// Post-tick (TryApplyPostTick): After landing on a slope, correct the player's velocity
/// to what it would have been if the collision had been handled at the precise collision point
/// rather than at the arbitrary tick boundary.
/// </summary>
public sealed class InclineFix
{
    private readonly RngFixConVars _conVars;
    private readonly IPhysicsSimulator _physics;
    private readonly ILogger<InclineFix> _logger;

    public InclineFix(RngFixConVars conVars, IPhysicsSimulator physics, ILogger<InclineFix> logger)
    {
        _conVars = conVars;
        _physics = physics;
        _logger  = logger;
    }

    // ──────────────────────────────── Pre-tick ────────────────────────────────

    /// <summary>
    /// Pre-tick: Prevents an uphill collision when it would negatively affect the player's speed
    /// and <see cref="RngFixConVars.UphillMode"/> is set to <see cref="PhysicsConstants.UphillNeutral"/>.
    /// </summary>
    /// <param name="state">Pre-tick player state.</param>
    /// <param name="velocity">Predicted pre-collision velocity.</param>
    /// <param name="origin">Player origin at tick start.</param>
    /// <param name="collisionPoint">Predicted collision point.</param>
    /// <param name="collisionNormal">Predicted collision surface normal.</param>
    /// <param name="moveOrigin">Reference to CMoveData origin — modified to rewind the player.</param>
    /// <returns>True if the fix was applied (caller should skip EdgeBugFix for this tick).</returns>
    public bool TryPreventUphill(
        ModulePlayerState state,
        in Vector velocity,
        in Vector origin,
        in Vector collisionPoint,
        in Vector collisionNormal,
        ref Vector moveOrigin)
    {
        if (_conVars.UphillMode != PhysicsConstants.UphillNeutral) return false;

        // Must be an inclined surface (not flat).
        if (collisionNormal.Z >= 1f) return false;

        // Must be going uphill: dot(normal.XY, velocity.XY) < 0
        float dot = collisionNormal.X * velocity.X + collisionNormal.Y * velocity.Y;
        if (dot >= 0f) return false;

        // If the downhill fix would actually be more beneficial here, let it handle this instead.
        if (_conVars.IsDownhillEnabled)
        {
            _physics.ClipVelocity(velocity, collisionNormal, out var clipped);
            float clippedXYSqr = clipped.X * clipped.X + clipped.Y * clipped.Y;
            float origXYSqr    = velocity.X * velocity.X + velocity.Y * velocity.Y;
            if (clippedXYSqr > origXYSqr) return false; // Downhill fix is better — skip.
        }

        _logger.LogDebug("InclineFix (uphill-neutral) applied at {CollisionPoint}", collisionPoint);
        PreventCollision(state, origin, collisionPoint, velocity, ref moveOrigin);
        return true;
    }

    // ──────────────────────────────── Post-tick ────────────────────────────────

    /// <summary>
    /// Post-tick: After landing on an inclined surface, correct the player's velocity to what
    /// it would have been if the collision had occurred at the exact mid-tick boundary.
    /// Handles both downhill (speed boost) and uphill-loss (speed penalty) modes.
    /// </summary>
    public bool TryApplyPostTick(ModulePlayerState state, IPlayerPawn pawn, in Vector landingNormal)
    {
        if (!_conVars.IsDownhillEnabled && _conVars.UphillMode != PhysicsConstants.UphillLoss) return false;
        if (state.LastTickPredicted != state.Tick) return false;

        // No point on level ground — collision would do nothing important.
        if (landingNormal.Z >= 1f) return false;

        // Nothing to do if moving upward unless we are in loss mode.
        if (state.PreCollisionVelocity.Z > 0f && _conVars.UphillMode != PhysicsConstants.UphillLoss) return false;

        // Skip if a collision was predicted this tick (already handled pre-tick), unless using
        // old slope fix logic which runs regardless.
        if (state.LastCollisionTick == state.Tick && !_conVars.UseOldSlopeFixLogic) return false;

        var velocity = state.PreCollisionVelocity;

        if (_conVars.UseOldSlopeFixLogic)
        {
            // Old logic: does not account for base velocity during clip calculation.
            velocity = new Vector(
                velocity.X - state.LastBaseVelocity.X,
                velocity.Y - state.LastBaseVelocity.Y,
                velocity.Z - state.LastBaseVelocity.Z);
        }

        float dot = landingNormal.X * velocity.X + landingNormal.Y * velocity.Y;

        if (dot >= 0f)
        {
            // Going downhill.
            if (!_conVars.IsDownhillEnabled) return false;
        }
        else
        {
            // Going uphill — only fix in loss mode, or if downhill fix is actually more beneficial.
            _physics.ClipVelocity(velocity, landingNormal, out var testClipped);
            bool downhillBeneficial = testClipped.X * testClipped.X + testClipped.Y * testClipped.Y
                                    > velocity.X * velocity.X + velocity.Y * velocity.Y;
            if (!((downhillBeneficial && _conVars.IsDownhillEnabled) || _conVars.UphillMode == PhysicsConstants.UphillLoss))
                return false;
        }

        _logger.LogDebug("InclineFix (post-tick) applied, normal.Z={NormalZ:F3}", landingNormal.Z);
        _physics.ClipVelocity(velocity, landingNormal, out var newVelocity);
        newVelocity = new Vector(newVelocity.X, newVelocity.Y, 0f); // On ground — no Z velocity.

        if (_conVars.UseOldSlopeFixLogic)
        {
            // Old logic: immediately absorbs base velocity to prevent it from being cleared.
            // This causes double-boost bugs when the source of base velocity is still active.
            EntityFlags flags = pawn.Flags;
            if (flags.HasFlag(EntityFlags.BaseVelocity))
            {
                var baseVel = pawn.BaseVelocity;
                newVelocity = new Vector(
                    newVelocity.X + baseVel.X,
                    newVelocity.Y + baseVel.Y,
                    newVelocity.Z + baseVel.Z);
            }

            // Old logic uses TeleportEntity directly (no base velocity subtraction).
            pawn.Teleport(null, null, newVelocity);
        }
        else
        {
            SetVelocity(pawn, newVelocity, state);
        }

        return true;
    }

    // ──────────────────────────────── Shared helpers ────────────────────────────────

    /// <summary>
    /// Rewrites CMoveData origin to just before the collision point, preventing the collision.
    /// Mirrors the original PreventCollision() logic.
    /// </summary>
    private static void PreventCollision(
        ModulePlayerState state,
        in Vector origin,
        in Vector collisionPoint,
        in Vector velocity,
        ref Vector moveOrigin)
    {
        var velocityTick = new Vector(
            velocity.X * state.FrameTime,
            velocity.Y * state.FrameTime,
            velocity.Z * state.FrameTime);

        var newOrigin = new Vector(
            collisionPoint.X - velocityTick.X,
            collisionPoint.Y - velocityTick.Y,
            collisionPoint.Z - velocityTick.Z + 0.1f);

        moveOrigin = newOrigin;
        state.LastCollisionTick = 0;
    }

    /// <summary>
    /// Sets the player's velocity, correctly accounting for base velocity.
    /// Mirrors the original SetVelocity() which subtracts pre-tick base velocity first.
    /// </summary>
    internal static void SetVelocity(IPlayerPawn pawn, Vector desiredVelocity, ModulePlayerState state, bool dontUseTeleport = false)
    {
        // The caller's desired velocity is the "true" velocity including base velocity effects.
        // Remove the base velocity contribution before setting so the engine doesn't double-add it.
        var velocity = new Vector(
            desiredVelocity.X - state.LastBaseVelocity.X,
            desiredVelocity.Y - state.LastBaseVelocity.Y,
            desiredVelocity.Z - state.LastBaseVelocity.Z);
        
        // TODO: SourceMod gates this path on m_hMoveParent == -1. We don't have a confirmed
        // CS2 equivalent here yet, so assume the pawn is unparented rather than use an
        // incorrect approximation like GroundEntity.

        if (dontUseTeleport)
        {
            // Directly set velocity — avoids side effects of TeleportEntity.
            pawn.SetAbsVelocity(velocity);
            pawn.SetLocalVelocity(velocity);
            // pawn.SetNetVar(PhysicsConstants.NetVarVelocity,  velocity);
        }
        else
        {
            // Use Teleport so the engine runs its full velocity reconciliation,
            // then restore base velocity which Teleport clears.
            var baseVelocity = pawn.BaseVelocity;
            pawn.Teleport(null, null, velocity);
            pawn.BaseVelocity = baseVelocity;
        }
    }
}
