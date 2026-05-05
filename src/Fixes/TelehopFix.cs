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

    // CS2 MASK_PLAYERSOLID equivalent
    private static readonly InteractionLayers PlayerSolidLayers =
        InteractionLayers.Solid      | InteractionLayers.Sky        | InteractionLayers.PlayerClip |
        InteractionLayers.WorldGeometry | InteractionLayers.Slime   | InteractionLayers.Player    |
        InteractionLayers.PhysicsProp;

    public TelehopFix(RngFixConVars conVars, IPhysicsSimulator physics, ILogger<TelehopFix> logger)
    {
        _conVars = conVars;
        _physics  = physics;
        _logger   = logger;
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

        // If the player appears to be stuck after teleporting (e.g. destination is flush with
        // the floor), set velocity directly to avoid TeleportEntity's side-effects causing a
        // deeper penetration into the ground.
        var origin = pawn.GetAbsOrigin();
        var cp     = pawn.GetCollisionProperty();
        var mins   = cp?.Mins ?? default;
        var maxs   = cp?.Maxs ?? default;

        var stuckQuery = RnQueryShapeAttr.PlayerMovement(PlayerSolidLayers);
        stuckQuery.SetEntityToIgnore(pawn, 0);
        var stuckTrace = physicsQuery.TraceShapePlayerMovement(
            new TraceShapeRay(new TraceShapeHull { Mins = mins, Maxs = maxs }),
            origin, origin,
            in stuckQuery);

        bool isStuck = stuckTrace.DidHit();

        _logger.LogDebug("TelehopFix applied (stuck={IsStuck})", isStuck);
        InclineFix.SetVelocity(pawn, newVelocity, state, dontUseTeleport: isStuck);
        return true;
    }
}
