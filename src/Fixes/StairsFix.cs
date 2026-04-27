using InsanityGaming.RngFix.Config;
using InsanityGaming.RngFix.Models;
using InsanityGaming.RngFix.Services;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Managers;
using Sharp.Shared.Types;

namespace InsanityGaming.RngFix.Fixes;

public sealed class StairsFix
{
    private readonly RngFixConVars _conVars;
    private readonly IPhysicsQueryManager _physicsQuery;
    private readonly IEntityManager _entityManager;
    private readonly TriggerNatives _triggerNatives;
    private readonly ILogger<StairsFix> _logger;

    private static readonly InteractionLayers PlayerSolidLayers =
        InteractionLayers.Solid | InteractionLayers.Sky | InteractionLayers.PlayerClip |
        InteractionLayers.WorldGeometry | InteractionLayers.Slime | InteractionLayers.Player |
        InteractionLayers.PhysicsProp;

    public StairsFix(
        RngFixConVars conVars,
        IPhysicsQueryManager physicsQuery,
        IEntityManager entityManager,
        TriggerNatives triggerNatives,
        ILogger<StairsFix> logger)
    {
        _conVars        = conVars;
        _physicsQuery   = physicsQuery;
        _entityManager  = entityManager;
        _triggerNatives = triggerNatives;
        _logger         = logger;
    }

    public bool TryApply(IPlayerPawn pawn, ModulePlayerState state)
    {
        if (!_conVars.IsStairsEnabled) return false;
        if (state.LastTickPredicted != state.Tick) return false;
        if (state.LastMapTeleportTick == state.Tick) return false;
        if (state.PreCollisionVelocity.Z > 0f) return false;
        if (state.LastCollisionTick != state.Tick) return false;
        if (state.CollisionNormal.Z != 0f) return false;

        float vx = state.PreCollisionVelocity.X;
        float vy = state.PreCollisionVelocity.Y;
        float hSpeed = MathF.Sqrt(vx * vx + vy * vy);

        if (hSpeed * state.FrameTime < 1f) return false;

        float dirX = vx / hSpeed;
        float dirY = vy / hSpeed;

        var cp = pawn.GetCollisionProperty();
        if (cp is null) return false;

        var mins = cp.Mins;
        var maxs = cp.Maxs;

        float stepSize = 18.0f;

        // ── Step 1: Trace down ──
        var collisionPoint = state.CollisionPoint;
        var stepBottom = new Vector(collisionPoint.X, collisionPoint.Y, collisionPoint.Z - stepSize);

        var downTrace = _physicsQuery.TraceShapeNoPlayers(
            new TraceShapeRay(new TraceShapeHull { Mins = mins, Maxs = maxs }),
            collisionPoint, stepBottom,
            PlayerSolidLayers, CollisionGroupType.Default, TraceQueryFlag.All);

        if (!downTrace.DidHit()) return false;
        if (downTrace.PlaneNormal.Z < PhysicsConstants.MinStandableZNrm) return false;

        var groundBelowStep = downTrace.EndPosition;

        // ── Step 2: Trigger check (FIXED) ──
        var triggerQuery = new RnQueryShapeAttr
        {
            m_nObjectSetMask = RnQueryObjectSet.All,
            m_nCollisionGroup = CollisionGroupType.Default,
            HitSolid = false,
            HitTrigger = true,
            ShouldIgnoreDisabledPairs = true,
            Unknown = true,
            m_nInteractsWith = InteractionLayers.Player,
            m_nInteractsAs = InteractionLayers.Player,
            m_nInteractsExclude = InteractionLayers.None,
        };

        var triggerHull = new TraceShapeHull { Mins = mins, Maxs = maxs };
        var triggerRay = new TraceShapeRay(triggerHull);

        Span<uint> entityBuffer = stackalloc uint[64];
        int count = _physicsQuery.EntitiesAlongRay(
            triggerRay,
            groundBelowStep,
            in triggerQuery,
            unique: true,
            entityBuffer);

        int entityCount = Math.Min(count, entityBuffer.Length);
        for (int i = 0; i < entityCount; i++)
        {
            var entity = TryGetEntity(entityBuffer[i]);
            if (entity is null) continue;

            var classname = entity.Classname;
            if (string.IsNullOrEmpty(classname) ||
                !classname.StartsWith("trigger_", StringComparison.OrdinalIgnoreCase))
                continue;

            if (_triggerNatives.PassesTriggerFilters(entity, pawn))
                return false;
        }

        // ── Step 3: Trace up ──
        var stepTop = new Vector(groundBelowStep.X, groundBelowStep.Y, groundBelowStep.Z + stepSize);

        var upTrace = _physicsQuery.TraceShapeNoPlayers(
            new TraceShapeRay(new TraceShapeHull { Mins = mins, Maxs = maxs }),
            groundBelowStep, stepTop,
            PlayerSolidLayers, CollisionGroupType.Default, TraceQueryFlag.All);

        var afterUp = upTrace.DidHit() ? upTrace.EndPosition : stepTop;

        // ── Step 4: Trace forward ──
        var overEnd = new Vector(afterUp.X + dirX, afterUp.Y + dirY, afterUp.Z);

        var overTrace = _physicsQuery.TraceShapeNoPlayers(
            new TraceShapeRay(new TraceShapeHull { Mins = mins, Maxs = maxs }),
            afterUp, overEnd,
            PlayerSolidLayers, CollisionGroupType.Default, TraceQueryFlag.All);

        if (overTrace.DidHit()) return false;

        // ── Step 5: Trace down ──
        var dropEnd = new Vector(overEnd.X, overEnd.Y, overEnd.Z - stepSize);

        var finalDownTrace = _physicsQuery.TraceShapeNoPlayers(
            new TraceShapeRay(new TraceShapeHull { Mins = mins, Maxs = maxs }),
            overEnd, dropEnd,
            PlayerSolidLayers, CollisionGroupType.Default, TraceQueryFlag.All);

        if (!finalDownTrace.DidHit()) return false;
        if (finalDownTrace.PlaneNormal.Z < PhysicsConstants.MinStandableZNrm) return false;

        var stepTopLanding = finalDownTrace.EndPosition;

        _logger.LogDebug("StairsFix applied at {StepTop}", stepTopLanding);

        pawn.Teleport(stepTopLanding, null, null);
        InclineFix.SetVelocity(pawn, state.PreCollisionVelocity, state);

        return true;
    }

    /// <summary>
    /// Safe entity lookup that prevents BaseEntity.Create crashes.
    /// </summary>
    private IBaseEntity? TryGetEntity(uint rawIndex)
    {
        if (rawIndex == 0 || rawIndex > 16384)
            return null;

        int index = (int)rawIndex;

        try
        {
            var entity = _entityManager.FindEntityByIndex(index);
            if (entity is null || !entity.IsValid())
                return null;

            return entity;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Invalid entity index {Index}", index);
            return null;
        }
    }
}
