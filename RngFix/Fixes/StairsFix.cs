using InsanityGaming.RngFix.Config;
using InsanityGaming.RngFix.Models;
using InsanityGaming.RngFix.Services;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Managers;
using Sharp.Shared.Types;

namespace InsanityGaming.RngFix.Fixes;

/// <summary>
/// Post-tick fix: Detects when the player collided with a stair-step face and, if the step
/// is climbable, teleports them to the top of the step while restoring their pre-collision
/// speed. Mirrors CGameMovement::StepMove detection logic.
///
/// This fix only applies when:
///   - A collision was predicted pre-tick with a perfectly vertical face (normal.Z == 0).
///   - The player was not teleported this tick (a teleport takes precedence).
///   - The player was moving fast enough (>= 1 unit per tick horizontally).
///   - The geometry below and above the face traces as a valid stair step.
/// </summary>
public sealed class StairsFix
{
    private readonly RngFixConVars _conVars;
    private readonly IPhysicsQueryManager _physicsQuery;
    private readonly IEntityManager _entityManager;
    private readonly TriggerNatives _triggerNatives;
    private readonly ILogger<StairsFix> _logger;

    // CS2 MASK_PLAYERSOLID equivalent
    private static readonly InteractionLayers PlayerSolidLayers =
        InteractionLayers.Solid      | InteractionLayers.Sky        | InteractionLayers.PlayerClip |
        InteractionLayers.WorldGeometry | InteractionLayers.Slime   | InteractionLayers.Player    |
        InteractionLayers.PhysicsProp;

    public StairsFix(RngFixConVars conVars, IPhysicsQueryManager physicsQuery, IEntityManager entityManager, TriggerNatives triggerNatives, ILogger<StairsFix> logger)
    {
        _conVars        = conVars;
        _physicsQuery   = physicsQuery;
        _entityManager  = entityManager;
        _triggerNatives = triggerNatives;
        _logger         = logger;
    }

    /// <summary>
    /// Evaluates whether the player hit a stair step this tick and, if so, moves them to
    /// the top of the step and restores their speed.
    /// </summary>
    /// <param name="pawn">The player pawn.</param>
    /// <param name="state">Current player state.</param>
    /// <returns>True if the fix was applied (caller should skip incline fixes for this tick).</returns>
    public bool TryApply(IPlayerPawn pawn, ModulePlayerState state)
    {
        if (!_conVars.IsStairsEnabled) return false;
        if (state.LastTickPredicted != state.Tick) return false;

        // Let teleports take precedence (including those fired by the trigger jump fix).
        if (state.LastMapTeleportTick == state.Tick) return false;

        // If moving upward, sliding up a step from the current position is impossible.
        if (state.PreCollisionVelocity.Z > 0f) return false;

        // Only act when the collision was with a perfectly vertical face (a stair riser).
        if (state.LastCollisionTick != state.Tick) return false;
        if (state.CollisionNormal.Z != 0f) return false;

        // Compute horizontal velocity direction (unit vector).
        float vx = state.PreCollisionVelocity.X;
        float vy = state.PreCollisionVelocity.Y;
        float hSpeed = MathF.Sqrt(vx * vx + vy * vy);

        // Skip if moving too slowly — less than 1 unit per tick horizontally.
        if (hSpeed * state.FrameTime < 1f) return false;

        float dirX = vx / hSpeed;
        float dirY = vy / hSpeed;

        var cp = pawn.GetCollisionProperty();
        if (cp is null) return false;

        var mins = cp.Mins;
        var maxs = cp.Maxs;
        
        float stepSize = 18.0f;

        // ── Step 1: Trace down from collision point to find ground below the step ──

        var collisionPoint = state.CollisionPoint;
        var stepBottom     = new Vector(collisionPoint.X, collisionPoint.Y, collisionPoint.Z - stepSize);

        var downTrace = _physicsQuery.TraceShapeNoPlayers(
            new TraceShapeRay(new TraceShapeHull { Mins = mins, Maxs = maxs }),
            collisionPoint, stepBottom,
            PlayerSolidLayers, CollisionGroupType.Default, TraceQueryFlag.All);

        if (!downTrace.DidHit()) return false;
        if (downTrace.PlaneNormal.Z < PhysicsConstants.MinStandableZNrm) return false; // Not walkable — not stairs.

        var groundBelowStep = downTrace.EndPosition;

        // ── Step 2: Check for triggers that would fire if we stepped there ──
        // If any trigger at the base of the step would activate for the player (e.g. a fail
        // teleport), skip the fix — it's more likely a ledge trap than actual stairs.
        var triggerQuery = new RnQueryShapeAttr
        {
            m_nObjectSetMask      = RnQueryObjectSet.All,
            m_nCollisionGroup     = CollisionGroupType.Default,
            HitSolid              = false,
            HitTrigger            = true,
            ShouldIgnoreDisabledPairs = true,
            Unknown               = true,
            m_nInteractsWith      = InteractionLayers.Player,
            m_nInteractsAs        = InteractionLayers.Player,
            m_nInteractsExclude   = InteractionLayers.None,
        };

        var triggerHull = new TraceShapeHull { Mins = mins, Maxs = maxs };
        var triggerRay  = new TraceShapeRay(triggerHull);

        Span<uint> entityBuffer = stackalloc uint[64];
        int count = _physicsQuery.EntitiesAlongRay(triggerRay, groundBelowStep, in triggerQuery, unique: true, entityBuffer);

        for (int i = 0; i < count; i++)
        {
            var entity = _entityManager.FindEntityByIndex((int)entityBuffer[i]);
            if (entity is null) continue;
            if (!entity.Classname.StartsWith("trigger_", StringComparison.OrdinalIgnoreCase)) continue;

            if (_triggerNatives.PassesTriggerFilters(entity, pawn))
                return false; // Blocking trigger found — ledge trap, not stairs.
        }

        // ── Step 3: Trace up from ground below step ──

        var stepTop = new Vector(groundBelowStep.X, groundBelowStep.Y, groundBelowStep.Z + stepSize);

        var upTrace = _physicsQuery.TraceShapeNoPlayers(
            new TraceShapeRay(new TraceShapeHull { Mins = mins, Maxs = maxs }),
            groundBelowStep, stepTop,
            PlayerSolidLayers, CollisionGroupType.Default, TraceQueryFlag.All);

        var afterUp = upTrace.DidHit() ? upTrace.EndPosition : stepTop;

        // ── Step 4: Trace over (1 unit in velocity direction) ──

        var overEnd = new Vector(afterUp.X + dirX, afterUp.Y + dirY, afterUp.Z);

        var overTrace = _physicsQuery.TraceShapeNoPlayers(
            new TraceShapeRay(new TraceShapeHull { Mins = mins, Maxs = maxs }),
            afterUp, overEnd,
            PlayerSolidLayers, CollisionGroupType.Default, TraceQueryFlag.All);

        if (overTrace.DidHit()) return false; // Ceiling too low or another wall — not a step we can climb.

        // ── Step 5: Trace back down to find the surface atop the step ──

        var dropEnd = new Vector(overEnd.X, overEnd.Y, overEnd.Z - stepSize);

        var finalDownTrace = _physicsQuery.TraceShapeNoPlayers(
            new TraceShapeRay(new TraceShapeHull { Mins = mins, Maxs = maxs }),
            overEnd, dropEnd,
            PlayerSolidLayers, CollisionGroupType.Default, TraceQueryFlag.All);

        if (!finalDownTrace.DidHit()) return false;
        if (finalDownTrace.PlaneNormal.Z < PhysicsConstants.MinStandableZNrm) return false; // Top surface not walkable.

        var stepTopLanding = finalDownTrace.EndPosition;

        // ── Apply fix: place player atop the step and restore pre-collision speed ──

        _logger.LogDebug("StairsFix applied at {StepTop}", stepTopLanding);
        pawn.Teleport(stepTopLanding, null, null);
        InclineFix.SetVelocity(pawn, state.PreCollisionVelocity, state);
        return true;
    }
}
