using InsanityGaming.RngFix.Config;
using InsanityGaming.RngFix.Models;
using InsanityGaming.RngFix.Services;
using InsanityGaming.RngFix.Utils;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Managers;
using Sharp.Shared.Types;

namespace InsanityGaming.RngFix.Fixes;

/// <summary>
/// Post-tick fix: When a player is teleported by a map trigger on the same tick they collide
/// with or land on a surface, restores their pre-collision velocity (minus the collision loss).
///
/// This prevents speed loss from the following sequence:
///   1. Player is about to collide (or land) — pre-tick state captured.
///   2. Engine processes movement: collision occurs, velocity reduced.
///   3. Engine fires trigger touches: a trigger_teleport fires, moving the player.
///   4. Post-tick: player is at new position but with the post-collision (reduced) velocity.
///
/// The fix restores the pre-collision velocity and applies the remaining half-tick of gravity
/// that would have been applied had the collision not interrupted the normal gravity sequence.
/// </summary>
public sealed class TelehopFix
{
    private readonly RngFixConVars _conVars;
    private readonly IPhysicsSimulator _physics;
    private readonly ILogger<TelehopFix> _logger;
    private readonly HitRateLogger _stuckHitLogger;


    public TelehopFix(RngFixConVars conVars, IPhysicsSimulator physics, ISharedSystem sharedSystem, ILogger<TelehopFix> logger)
    {
        _conVars        = conVars;
        _physics        = physics;
        _logger         = logger;
        _stuckHitLogger = new HitRateLogger("TelehopFix.Stuck", sharedSystem, logger, 0f);
    }

    /// <summary>
    /// Checks whether telehop conditions are met and, if so, restores the player's
    /// pre-collision velocity (plus second half-tick gravity).
    /// </summary>
    /// <param name="pawn">The player pawn.</param>
    /// <param name="state">Current player state.</param>
    /// <param name="physicsQuery">Physics query manager (used to detect stuck-in-ground).</param>
    /// <returns>True if the fix was applied.</returns>
    public bool TryApply(IPlayerPawn pawn, ModulePlayerState state, IPhysicsQueryManager physicsQuery)
    {
        if (!_conVars.IsTelehopEnabled) return false;
        if (state.LastTickPredicted != state.Tick) return false;

        // Must have been teleported by a map trigger this tick.
        if (state.LastMapTeleportTick != state.Tick) return false;

        // Two consecutive teleport ticks likely means a speed-stopping hub — don't restore speed.
        if (state.MapTeleportedSequentialTicks) return false;

        // Must have either collided or landed this tick (otherwise there's nothing to restore).
        bool collidedThisTick = state.LastCollisionTick == state.Tick;
        bool landedThisTick   = state.LastLandTick       == state.Tick;
        if (!collidedThisTick && !landedThisTick) return false;

        // Start from pre-collision velocity and apply the second half-tick of gravity.
        var newVelocity = state.PreCollisionVelocity;
        _physics.FinishGravity(state, pawn, ref newVelocity);

        // Never restore upward Z velocity through a teleport. TelehopFix exists to preserve
        // horizontal speed; positive Z at the destination would let players gain height at
        // reset spawns by holding jump while ascending slowly (0 < Z ≤ NonJumpVelocity).
        if (newVelocity.Z > 0f)
            newVelocity = new Vector(newVelocity.X, newVelocity.Y, 0f);

        // If the player appears to be stuck after teleporting (e.g. destination is flush with
        // the floor), set velocity directly to avoid TeleportEntity's side-effects causing a
        // deeper penetration into the ground.
        var origin = pawn.GetAbsOrigin();
        var cp     = pawn.GetCollisionProperty();
        var mins   = cp?.Mins ?? default;
        var maxs   = cp?.Maxs ?? default;

        var stuckQuery = RnQueryShapeAttr.PlayerMovement(PhysicsConstants.PlayerSolidLayers);
        stuckQuery.SetEntityToIgnore(pawn, 0);
        var stuckTrace = physicsQuery.TraceShapePlayerMovement(
            new TraceShapeRay(new TraceShapeHull { Mins = mins, Maxs = maxs }),
            origin, origin,
            in stuckQuery);

        bool isStuck = stuckTrace.DidHit();

        if (isStuck)
        {
            _logger.LogDebug("TelehopFix stuck hit — InteractsAs={A} InteractsWith={W} Group={G}",
                stuckTrace.ShapeAttributes.InteractsAs,
                stuckTrace.ShapeAttributes.InteractsWith,
                stuckTrace.ShapeAttributes.CollisionGroup);
            _stuckHitLogger.Record(
                pawn.Index,
                stuckTrace.ShapeAttributes.InteractsAs,
                stuckTrace.ShapeAttributes.InteractsWith,
                stuckTrace.ShapeAttributes.CollisionGroup);
        }

        // Safety check: trace the restored velocity for one tick from the post-teleport origin.
        // If the path is immediately blocked (very low fraction), the player is likely at a reset
        // spawn pressed against solid geometry — skip restoration to avoid pushing them into it.
        var safetyEnd = new Vector(
            origin.X + newVelocity.X * state.FrameTime,
            origin.Y + newVelocity.Y * state.FrameTime,
            origin.Z + newVelocity.Z * state.FrameTime);
        var safetyQuery = RnQueryShapeAttr.PlayerMovement(PhysicsConstants.PlayerSolidLayers);
        safetyQuery.SetEntityToIgnore(pawn, 0);
        var safetyTrace = physicsQuery.TraceShapePlayerMovement(
            new TraceShapeRay(new TraceShapeHull { Mins = mins, Maxs = maxs }),
            origin, safetyEnd,
            in safetyQuery);

        if (safetyTrace.Fraction < 0.1f)
        {
            _logger.LogDebug("TelehopFix skipped — safety trace blocked (fraction={F:F3})", safetyTrace.Fraction);
            return false;
        }

        _logger.LogDebug("TelehopFix applied (stuck={IsStuck})", isStuck);
        InclineFix.SetVelocity(pawn, newVelocity, state, dontUseTeleport: isStuck);
        return true;
    }
}
