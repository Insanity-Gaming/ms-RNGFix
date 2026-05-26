using InsanityGaming.RngFix.Config;
using InsanityGaming.RngFix.Models;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Managers;
using Sharp.Shared.Types;

namespace InsanityGaming.RngFix.Fixes;

/// <summary>
/// Pre-tick fix: Prevents edge bugs by detecting when the player will narrowly miss landing
/// at the end of the tick and rewinding their origin to guarantee the landing.
///
/// An edge bug occurs when the player grazes a surface mid-tick, gets deflected upward
/// (Z velocity capped), and ends the tick in the air just above ground they should have landed on.
/// </summary>
public sealed class EdgeBugFix
{
    private readonly RngFixConVars _conVars;
    private readonly IPhysicsQueryManager _physicsQuery;
    private readonly ILogger<EdgeBugFix> _logger;


    public EdgeBugFix(RngFixConVars conVars, IPhysicsQueryManager physicsQuery, ILogger<EdgeBugFix> logger)
    {
        _conVars      = conVars;
        _physicsQuery = physicsQuery;
        _logger       = logger;
    }

    /// <summary>
    /// Evaluates whether the predicted collision at <paramref name="collisionPoint"/> will result
    /// in an edge bug, and if so, rewrites <paramref name="moveOrigin"/> in CMoveData to prevent it.
    /// </summary>
    /// <param name="state">Current player state (pre-tick).</param>
    /// <param name="velocity">Pre-collision velocity (with gravity and jump applied).</param>
    /// <param name="origin">Player origin at tick start.</param>
    /// <param name="collisionPoint">Predicted first collision point.</param>
    /// <param name="collisionNormal">Surface normal at collision point.</param>
    /// <param name="mins">Player hull mins (accounting for duck state).</param>
    /// <param name="maxs">Player hull maxs (accounting for duck state).</param>
    /// <param name="moveOrigin">Reference to CMoveData origin — set this to rewind the player.</param>
    /// <returns>True if the fix was applied.</returns>
    public unsafe bool TryPrevent(
        IPlayerPawn pawn,
        ModulePlayerState state,
        in Vector velocity,
        in Vector origin,
        in Vector collisionPoint,
        in Vector collisionNormal,
        in Vector mins,
        in Vector maxs,
        ref Vector moveOrigin)
    {
        if (!_conVars.IsEdgeEnabled) return false;

        // Skip if the player is inside a trigger_teleport — PreventCollision places the rewound
        // origin above the collision point (falling velocity is negative, so subtracting it adds
        // height). This elevated AbsOrigin shifts the player's offset in relative teleports,
        // causing them to arrive at the destination too high and clip into the ceiling.
        if (state.TouchingTeleportTriggerCount > 0) return false;

        // Estimate where the player will end up at tick end after the collision.
        var fractionQuery = RnQueryShapeAttr.PlayerMovement(PhysicsConstants.PlayerSolidLayers);
        fractionQuery.SetEntityToIgnore(pawn, 0);
        var fractionTrace = _physicsQuery.TraceShapePlayerMovement(
            new TraceShapeRay(new TraceShapeHull { Mins = mins, Maxs = maxs }),
            origin, collisionPoint,
            in fractionQuery);
        if (fractionTrace.DidHit())
            _logger.LogDebug("EdgeBug fraction hit — InteractsAs={A} InteractsWith={W} Group={G}",
                fractionTrace.ShapeAttributes.InteractsAs,
                fractionTrace.ShapeAttributes.InteractsWith,
                fractionTrace.ShapeAttributes.CollisionGroup);
        float fractionLeft = 1f - fractionTrace.Fraction;

        Vector tickEnd;

        if (Math.Abs(collisionNormal.Z - 1f) < 0.01)
        {
            // Level ground: all that changes after collision is Z velocity becomes zero.
            var velocityTick = new Vector(velocity.X * state.FrameTime, velocity.Y * state.FrameTime, velocity.Z * state.FrameTime);
            tickEnd = new Vector(
                collisionPoint.X + velocity.X * state.FrameTime * fractionLeft,
                collisionPoint.Y + velocity.Y * state.FrameTime * fractionLeft,
                collisionPoint.Z);
        }
        else
        {
            // Inclined surface: deflect velocity and project the rest of the tick.
            var deflected = new Vector(
                velocity.X - collisionNormal.X * (velocity.X * collisionNormal.X + velocity.Y * collisionNormal.Y + velocity.Z * collisionNormal.Z),
                velocity.Y - collisionNormal.Y * (velocity.X * collisionNormal.X + velocity.Y * collisionNormal.Y + velocity.Z * collisionNormal.Z),
                velocity.Z - collisionNormal.Z * (velocity.X * collisionNormal.X + velocity.Y * collisionNormal.Y + velocity.Z * collisionNormal.Z));

            if (deflected.Z > PhysicsConstants.NonJumpVelocity)
            {
                // Would be an edge bug 100% of the time — always airborne at tick end.
                return false;
            }

            tickEnd = new Vector(
                collisionPoint.X + deflected.X * state.FrameTime * fractionLeft,
                collisionPoint.Y + deflected.Y * state.FrameTime * fractionLeft,
                collisionPoint.Z + deflected.Z * state.FrameTime * fractionLeft);
        }

        // Check if there is something to land on within LAND_HEIGHT below the estimated tick end.
        var tickEndBelow = new Vector(tickEnd.X, tickEnd.Y, tickEnd.Z - PhysicsConstants.LandHeight);
        var groundQuery  = RnQueryShapeAttr.PlayerMovement(PhysicsConstants.PlayerSolidLayers);
        groundQuery.SetEntityToIgnore(pawn, 0);
        var groundTrace  = _physicsQuery.TraceShapePlayerMovement(
            new TraceShapeRay(new TraceShapeHull { Mins = mins, Maxs = maxs }),
            tickEnd, tickEndBelow,
            in groundQuery);

        if (groundTrace.DidHit())
        {
            _logger.LogDebug("EdgeBug ground hit — InteractsAs={A} InteractsWith={W} Group={G}",
                groundTrace.ShapeAttributes.InteractsAs,
                groundTrace.ShapeAttributes.InteractsWith,
                groundTrace.ShapeAttributes.CollisionGroup);
            // There's ground nearby — check if it's actually landable.
            var nrm2 = groundTrace.PlaneNormal;
            if (nrm2.Z >= PhysicsConstants.MinStandableZNrm) return false;           // Landable — no edge bug.
            if (TracePlayerBBoxForGround(pawn, tickEnd, tickEndBelow, mins, maxs)) return false; // Quadrant check also finds ground.
        }

        // The player will not land. Rewind origin to prevent the collision.
        _logger.LogDebug("EdgeBugFix applied at {CollisionPoint}", collisionPoint);
        PreventCollision(state, origin, collisionPoint, velocity, ref moveOrigin);
        return true;
    }

    // ──────────────────────────────── Private helpers ────────────────────────────────

    /// <summary>
    /// Rewrites CMoveData origin so the player ends the tick just above the collision surface
    /// rather than passing through it. Effectively simulates a partial-tick jump.
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

        // Rewind: place the player before where the collision would happen.
        var newOrigin = new Vector(
            collisionPoint.X - velocityTick.X,
            collisionPoint.Y - velocityTick.Y,
            collisionPoint.Z - velocityTick.Z + 0.1f); // small clearance to avoid floating-point collision

        moveOrigin = newOrigin;
        state.LastCollisionTick = 0; // No longer colliding this tick — clear prediction flag.
    }

    /// <summary>
    /// Checks four hull quadrants below the player to find walkable ground on steep surfaces.
    /// Mirrors CGameMovement::TracePlayerBBoxForGround.
    /// </summary>
    private bool TracePlayerBBoxForGround(IPlayerPawn pawn, in Vector origin, in Vector originBelow, in Vector mins, in Vector maxs)
    {
        // -x -y quadrant
        var q1Maxs = new Vector(maxs.X > 0f ? 0f : maxs.X, maxs.Y > 0f ? 0f : maxs.Y, maxs.Z);
        if (HullGroundHit(pawn, origin, originBelow, mins, q1Maxs)) return true;

        // +x +y quadrant
        var q2Mins = new Vector(mins.X < 0f ? 0f : mins.X, mins.Y < 0f ? 0f : mins.Y, mins.Z);
        if (HullGroundHit(pawn, origin, originBelow, q2Mins, maxs)) return true;

        // -x +y quadrant
        var q3Mins = new Vector(mins.X, mins.Y < 0f ? 0f : mins.Y, mins.Z);
        var q3Maxs = new Vector(maxs.X > 0f ? 0f : maxs.X, maxs.Y, maxs.Z);
        if (HullGroundHit(pawn, origin, originBelow, q3Mins, q3Maxs)) return true;

        // +x -y quadrant
        var q4Mins = new Vector(mins.X < 0f ? 0f : mins.X, mins.Y, mins.Z);
        var q4Maxs = new Vector(maxs.X, maxs.Y > 0f ? 0f : maxs.Y, maxs.Z);
        if (HullGroundHit(pawn, origin, originBelow, q4Mins, q4Maxs)) return true;

        return false;
    }

    private bool HullGroundHit(IPlayerPawn pawn, in Vector from, in Vector to, in Vector mins, in Vector maxs)
    {
        var query = RnQueryShapeAttr.PlayerMovement(PhysicsConstants.PlayerSolidLayers);
        query.SetEntityToIgnore(pawn, 0);
        var trace = _physicsQuery.TraceShapePlayerMovement(
            new TraceShapeRay(new TraceShapeHull { Mins = mins, Maxs = maxs }),
            from, to,
            in query);

        bool hit = trace.DidHit() && trace.PlaneNormal.Z >= PhysicsConstants.MinStandableZNrm;
        if (hit)
            _logger.LogDebug("EdgeBug hull quadrant hit — InteractsAs={A} InteractsWith={W} Group={G}",
                trace.ShapeAttributes.InteractsAs,
                trace.ShapeAttributes.InteractsWith,
                trace.ShapeAttributes.CollisionGroup);
        return hit;
    }
}
