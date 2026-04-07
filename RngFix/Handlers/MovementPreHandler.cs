using InsanityGaming.RngFix.Config;
using InsanityGaming.RngFix.Fixes;
using InsanityGaming.RngFix.Models;
using InsanityGaming.RngFix.Services;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.HookParams;
using Sharp.Shared.Managers;
using Sharp.Shared.Types;

namespace InsanityGaming.RngFix.Handlers;

/// <summary>
/// Installed as a PlayerProcessMovePre forward. Runs once per player per game tick before
/// the engine processes movement. Simulates enough of CGameMovement::ProcessMovement to predict
/// whether the player will collide with a surface this tick, then applies pre-tick fixes.
/// </summary>
public sealed class MovementPreHandler : IRngModule
{
    private readonly ISharedSystem _sharedSystem;
    private readonly IPlayerStateService _playerState;
    private readonly IPhysicsSimulator _physics;
    private readonly IPhysicsQueryManager _physicsQuery;
    private readonly RngFixConVars _conVars;
    private readonly EdgeBugFix _edgeBugFix;
    private readonly InclineFix _inclineFix;
    private readonly ILogger<MovementPreHandler> _logger;

    // CS2 MASK_PLAYERSOLID equivalent
    private static readonly InteractionLayers PlayerSolidLayers =
        InteractionLayers.Solid      | InteractionLayers.Sky        | InteractionLayers.PlayerClip |
        InteractionLayers.WorldGeometry | InteractionLayers.Slime   | InteractionLayers.Player    |
        InteractionLayers.PhysicsProp;

    public MovementPreHandler(
        ISharedSystem sharedSystem,
        IPlayerStateService playerState,
        IPhysicsSimulator physics,
        IPhysicsQueryManager physicsQuery,
        RngFixConVars conVars,
        EdgeBugFix edgeBugFix,
        InclineFix inclineFix,
        ILogger<MovementPreHandler> logger)
    {
        _sharedSystem = sharedSystem;
        _playerState  = playerState;
        _physics      = physics;
        _physicsQuery = physicsQuery;
        _conVars      = conVars;
        _edgeBugFix   = edgeBugFix;
        _inclineFix   = inclineFix;
        _logger       = logger;
    }

    public bool Init()
    {
        _sharedSystem.GetHookManager().PlayerProcessMovePre.InstallForward(OnProcessMovementPre);
        return true;
    }

    public void Shutdown() { }

    /// <summary>
    /// Called by the PlayerProcessMovePre hook. Entry point for all pre-tick logic.
    /// </summary>
    public unsafe void OnProcessMovementPre(IPlayerProcessMoveForwardParams obj)
    {
        // Get the player pawn from the hook params.
        var pawn = obj.Pawn;

        int entityIndex = pawn.Index;
        var state = _playerState.GetOrCreate(entityIndex);

        // Always increment tick and clear per-tick state.
        state.Tick++;
        state.FrameTime = GetTickInterval() * 1.0f;
        state.MapTeleportedSequentialTicks = false;

        // Fast-path: skip all prediction if no pre-tick fix is active.
        if (!_conVars.AnyPreTickFixEnabled) return;

        RunPreTickChecks(pawn, state, obj);
    }

    // ──────────────────────────────── Private ────────────────────────────────

    private unsafe void RunPreTickChecks(IPlayerPawn pawn, ModulePlayerState state, IPlayerProcessMoveForwardParams obj)
    {
        if (!pawn.IsAlive) return;
        if (pawn.ActualMoveType != MoveType.Walk) return;
        if (_physics.IsInWater(pawn)) return;

        // Record the ground entity BEFORE movement so PostThink can detect landing.
        state.PreTickGroundEnt = pawn.GroundEntity?.Index ?? -1;

        // If solidly on the ground and not attempting to jump, no collision possible.
        bool wantsJump = obj.Service.KeyButtons.HasFlag(UserCommandButtons.Jump);
        if (state.PreTickGroundEnt != -1 && !wantsJump) return;

        // Record tick for post-think fix gating.
        state.LastTickPredicted = state.Tick;

        // Capture CMoveData inputs.
        state.Buttons        = obj.Service.KeyButtons;
        state.ChangedButtons = obj.Service.KeyChangedButtons;
        state.ForwardMove    = obj.Info->ForwardMove;
        state.SideMove       = obj.Info->SideMove;
        state.AnglePitch     = obj.Info->ViewAngles.X;
        state.AngleYaw       = obj.Info->ViewAngles.Y;

        // Capture velocity and origin from CMoveData.
        var velocity   = obj.Info->Velocity;
        var origin     = obj.Info->AbsOrigin;
        var nextOrigin = origin;

        // Read base velocity directly from the entity (not in CMoveData).
        var baseVelocity = pawn.GetAbsVelocity();

        // ── Replicate CGameMovement::ProcessMovement math ──

        // 1. Simulate duck — adjusts nextOrigin and produces hull mins/maxs.
        _physics.SimulateDuck(state, pawn, ref nextOrigin, out var mins, out var maxs);

        // 2. Apply first half-tick of gravity.
        _physics.StartGravity(state, pawn, ref velocity);

        // 3. Simulate jump button (applies impulse + second half of gravity if jumping).
        _physics.CheckJumpButton(state, pawn, ref velocity);

        // 4. Clamp velocity to sv_maxvelocity.
        _physics.CheckVelocity(ref velocity);

        // 5. Apply air acceleration from player input.
        _physics.AirAccelerate(state, ref velocity, obj.Info->MaxSpeed);

        // 6. Incorporate XY base velocity; store for post-think SetVelocity corrections.
        state.LastBaseVelocity = new Vector(baseVelocity.X, baseVelocity.Y, 0f);
        velocity = new Vector(
            velocity.X + baseVelocity.X,
            velocity.Y + baseVelocity.Y,
            velocity.Z);

        // Store the pre-collision velocity for post-think fixes (telehop, incline).
        state.PreCollisionVelocity = velocity;

        // ── Predict collision ──

        // Project one tick of movement and trace the hull.
        var velocityTick = new Vector(velocity.X * state.FrameTime, velocity.Y * state.FrameTime, velocity.Z * state.FrameTime);
        var traceEnd     = new Vector(nextOrigin.X + velocityTick.X, nextOrigin.Y + velocityTick.Y, nextOrigin.Z + velocityTick.Z);

        var trace = _physicsQuery.TraceShapeNoPlayers(
            new TraceShapeRay(new TraceShapeHull { Mins = mins, Maxs = maxs }),
            nextOrigin, traceEnd,
            PlayerSolidLayers, CollisionGroupType.Default, TraceQueryFlag.All);

        if (!trace.DidHit()) return;

        var nrm            = trace.PlaneNormal;
        var collisionPoint = trace.EndPosition;

        // Record the predicted collision for post-think gating.
        state.LastCollisionTick = state.Tick;
        state.CollisionPoint    = collisionPoint;
        state.CollisionNormal   = nrm;

        // If the player is moving up too fast they cannot land regardless — skip fixes.
        if (velocity.Z > PhysicsConstants.NonJumpVelocity) return;

        // Only fix collisions with walkable surfaces.
        if (nrm.Z < PhysicsConstants.MinStandableZNrm) return;

        // ── Apply pre-tick fixes ──

        // Capture CMoveData origin so we can modify it if a fix applies.
        var moveOrigin = obj.Info->AbsOrigin;

        // Try uphill-neutral fix first (more common, faster to check).
        if (_inclineFix.TryPreventUphill(state, velocity, origin, collisionPoint, nrm, ref moveOrigin))
        {
            obj.Info->AbsOrigin = moveOrigin;
            return; // Uphill fix already prevents any edge bug on this tick.
        }

        // Try edge bug fix.
        if (_edgeBugFix.TryPrevent(state, velocity, origin, collisionPoint, nrm, mins, maxs, ref moveOrigin))
        {
            obj.Info->AbsOrigin = moveOrigin;
        }
    }

    /// <summary>
    /// Returns the server tick interval in seconds.
    /// </summary>
    private static float GetTickInterval() => 1f / 64f; // CS2 default: 64 tick
}
