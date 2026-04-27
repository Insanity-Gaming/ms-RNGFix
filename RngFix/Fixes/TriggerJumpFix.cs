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
/// Post-tick fix: Fires trigger touch events for triggers that were between the player
/// and the ground when they landed, which the engine may have skipped due to tick-boundary
/// rounding. Prevents scenarios where landing "above" a trigger (e.g. a jump trigger) due
/// to collision processing order causes the trigger to be missed entirely.
/// </summary>
public sealed class TriggerJumpFix
{
    private readonly RngFixConVars _conVars;
    private readonly IPhysicsQueryManager _physicsQuery;
    private readonly IEntityManager _entityManager;
    private readonly ITriggerTracker _triggerTracker;
    private readonly ITriggerTouchSynthesizer _triggerTouchSynthesizer;
    private readonly ILogger<TriggerJumpFix> _logger;

    public TriggerJumpFix(
        RngFixConVars conVars,
        IPhysicsQueryManager physicsQuery,
        IEntityManager entityManager,
        ITriggerTracker triggerTracker,
        ITriggerTouchSynthesizer triggerTouchSynthesizer,
        ILogger<TriggerJumpFix> logger)
    {
        _conVars                 = conVars;
        _physicsQuery            = physicsQuery;
        _entityManager           = entityManager;
        _triggerTracker          = triggerTracker;
        _triggerTouchSynthesizer = triggerTouchSynthesizer;
        _logger                  = logger;
    }

    /// <summary>
    /// After a player lands, scan for triggers between the player and the landing point and
    /// fire them if they were not already touched this tick.
    /// </summary>
    public bool TryApply(IPlayerPawn pawn, in Vector landingPoint, in Vector landingMins, in Vector landingMaxs)
    {
        if (!_conVars.IsTriggerJumpEnabled)
            return false;

        var origin = pawn.GetAbsOrigin();

        // Build vertical hull from landing point → player origin
        var hullMaxs = new Vector(
            landingMaxs.X,
            landingMaxs.Y,
            origin.Z - landingPoint.Z);

        var query = new RnQueryShapeAttr
        {
            m_nObjectSetMask          = RnQueryObjectSet.All,
            m_nCollisionGroup         = CollisionGroupType.Default,
            HitSolid                  = false,
            HitTrigger                = true,
            ShouldIgnoreDisabledPairs = true,
            Unknown                   = true,
            m_nInteractsWith          = InteractionLayers.Player,
            m_nInteractsAs            = InteractionLayers.Player,
            m_nInteractsExclude       = InteractionLayers.None,
        };

        var hull = new TraceShapeHull { Mins = landingMins, Maxs = hullMaxs };
        var ray  = new TraceShapeRay(hull);

        Span<uint> entityBuffer = stackalloc uint[128];

        int count = _physicsQuery.EntitiesAlongRay(ray, landingPoint, in query, unique: true, entityBuffer);

        bool didSomething = false;

        for (int i = 0; i < count; i++)
        {
            int idx = (int)entityBuffer[i];

            if (idx <= 0)
                continue;

            IBaseEntity? entity;

            try
            {
                entity = _entityManager.FindEntityByIndex(idx);
            }
            catch
            {
                continue;
            }

            if (entity is null || !entity.IsValid())
                continue;

            string? classname;
            try
            {
                classname = entity.Classname;
            }
            catch
            {
                continue;
            }

            if (string.IsNullOrEmpty(classname) ||
                !classname.StartsWith("trigger_", StringComparison.OrdinalIgnoreCase))
                continue;

            int triggerIdx = entity.Index;

            // Skip if already touching this tick
            if (_triggerTracker.IsTouching(pawn.Index, triggerIdx))
                continue;

            _logger.LogDebug("TriggerJumpFix applied for trigger {TriggerIdx}", triggerIdx);
            _triggerTouchSynthesizer.EnsureTouchAfterManualTrigger(entity, pawn);

            didSomething = true;
        }

        return didSomething;
    }
}
