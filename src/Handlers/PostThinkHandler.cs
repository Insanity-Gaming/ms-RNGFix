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
/// Installed as a PlayerPostThink forward. Runs once per player per game tick AFTER the
/// engine has processed movement and fired trigger touches. Applies all post-tick fixes.
///
/// PostThink is preferred over a ProcessMovement post hook because we need trigger activations
/// (including teleports) to have already been processed before we inspect ground state and
/// decide whether to apply velocity corrections.
/// </summary>
public sealed class PostThinkHandler : IRngModule
{
    private readonly ISharedSystem _sharedSystem;
    private readonly IPlayerStateService _playerState;
    private readonly IPhysicsQueryManager _physicsQuery;
    private readonly RngFixConVars _conVars;
    private readonly TriggerJumpFix _triggerJumpFix;
    private readonly StairsFix _stairsFix;
    private readonly InclineFix _inclineFix;
    private readonly TelehopFix _telehopFix;
    private readonly ILogger<PostThinkHandler> _logger;

    // CS2 MASK_PLAYERSOLID equivalent
    private static readonly InteractionLayers PlayerSolidLayers =
        InteractionLayers.Solid      | InteractionLayers.Sky        | InteractionLayers.PlayerClip |
        InteractionLayers.WorldGeometry | InteractionLayers.Slime   | InteractionLayers.Player    |
        InteractionLayers.PhysicsProp;

    public PostThinkHandler(
        ISharedSystem sharedSystem,
        IPlayerStateService playerState,
        IPhysicsQueryManager physicsQuery,
        RngFixConVars conVars,
        TriggerJumpFix triggerJumpFix,
        StairsFix stairsFix,
        InclineFix inclineFix,
        TelehopFix telehopFix,
        ILogger<PostThinkHandler> logger)
    {
        _sharedSystem   = sharedSystem;
        _playerState    = playerState;
        _physicsQuery   = physicsQuery;
        _conVars        = conVars;
        _triggerJumpFix = triggerJumpFix;
        _stairsFix      = stairsFix;
        _inclineFix     = inclineFix;
        _telehopFix     = telehopFix;
        _logger         = logger;
    }

    public bool Init()
    {
        _sharedSystem.GetHookManager().PlayerPostThink.InstallForward(OnPostThink);
        return true;
    }

    public void Shutdown() { }

    /// <summary>
    /// Called by the PlayerPostThink hook. Entry point for all post-tick logic.
    /// </summary>
    public void OnPostThink(IPlayerThinkForwardParams playerThinkForwardParams)
    {
        var pawn = playerThinkForwardParams.Pawn;
        if (pawn is null) return;
        if (!pawn.IsAlive) return;
        if (pawn.ActualMoveType != MoveType.Walk) return;
        if (IsInWater(pawn)) return;

        int entityIndex = pawn.Index;
        var state = _playerState.Get(entityIndex);
        if (state is null) return;

        // ── Detect landing ──
        // Compare current ground entity to what it was before the tick (saved pre-tick).
        bool landed = state.LastLandTick == state.Tick;


        // ── Gather landing surface info (needed by TriggerJumpFix and InclineFix) ──
        bool needLandingInfo = landed &&
            (_conVars.IsTriggerJumpEnabled || _conVars.IsDownhillEnabled ||
             _conVars.UphillMode == PhysicsConstants.UphillLoss);

        Vector landingNormal  = default;
        Vector landingPoint   = default;
        Vector landingMins    = default;
        Vector landingMaxs    = default;
        float landingFraction = 0f;
        bool    landingInfoOk  = false;

        if (needLandingInfo)
        {
            var origin = pawn.GetAbsOrigin();
            var cp = pawn.GetCollisionProperty();
            landingMins = cp?.Mins ?? default;
            landingMaxs = cp?.Maxs ?? default;

            var originBelow = new Vector(origin.X, origin.Y, origin.Z - PhysicsConstants.LandHeight);

            var groundQuery = RnQueryShapeAttr.PlayerMovement(PlayerSolidLayers);
            groundQuery.SetEntityToIgnore(pawn, 0);
            var groundTrace = _physicsQuery.TraceShapePlayerMovement(
                new TraceShapeRay(new TraceShapeHull { Mins = landingMins, Maxs = landingMaxs }),
                origin, originBelow,
                in groundQuery);

            if (!groundTrace.DidHit())
            {
                // We believe we landed but can't find ground — treat as not landed.
                landed = false;
            }
            else
            {
                landingNormal = groundTrace.PlaneNormal;
                landingPoint  = groundTrace.EndPosition;
                landingFraction = groundTrace.Fraction;

                // If the primary trace hit a non-walkable face, fall back to quadrant traces
                // (mirrors CGameMovement::TracePlayerBBoxForGround).
                if (landingNormal.Z < PhysicsConstants.MinStandableZNrm)
                {
                    bool found = TryQuadrantGroundTrace(
                        pawn,
                        origin, originBelow, landingMins, landingMaxs,
                        out landingNormal, out landingPoint, out landingMins, out landingMaxs, out landingFraction);

                    if (!found)
                    {
                        landed = false;
                    }
                }

                if (landed && landingFraction > 0f)
                {
                    landingInfoOk = true;
                }
                else if (landed)
                {
                    // Fraction == 0 means we started inside the ground — don't apply fixes.
                    landed = false;
                }
            }
        }

        // ── TriggerJump fix ──
        // Must run before stair/incline fixes. If a trigger teleports us, we re-check ground state.
        if (landingInfoOk)
        {
            _triggerJumpFix.TryApply(pawn, landingPoint, landingMins, landingMaxs);

            // After trigger jump fix: a teleport may have lifted us off the ground.
            if (!pawn.Flags.HasFlag(EntityFlags.OnGround))
            {
                landed = false;
            }
        }

        // ── Stairs fix ──
        // If stairs applies, skip incline fixes for this tick (stairs changes position more
        // significantly, making an additional velocity correction nonsensical).
        if (_stairsFix.TryApply(pawn, state))
        {
            _telehopFix.TryApply(pawn, state, _physicsQuery);
            return;
        }

        // ── Incline fix (post-tick) ──
        if (landed)
        {
            _inclineFix.TryApplyPostTick(state, pawn, landingNormal);
        }
        

        // ── Telehop fix ──
        _telehopFix.TryApply(pawn, state, _physicsQuery);
    }

    // ──────────────────────────────── Private helpers ────────────────────────────────

    private bool IsInWater(IPlayerPawn pawn) => pawn.GetNetVar<float>(PhysicsConstants.NetVarWaterLevel) > 1f;

    /// <summary>
    /// Tries the four hull quadrants (mirrors CGameMovement::TracePlayerBBoxForGround) to
    /// locate walkable ground when the primary full-hull trace hits a non-walkable face.
    /// Updates <paramref name="usedMins"/> and <paramref name="usedMaxs"/> to the quadrant
    /// that found ground (needed for TriggerJumpFix hull construction).
    /// </summary>
    private bool TryQuadrantGroundTrace(
        IPlayerPawn pawn,
        in Vector origin,
        in Vector originBelow,
        in Vector origMins,
        in Vector origMaxs,
        out Vector normal,
        out Vector point,
        out Vector usedMins,
        out Vector usedMaxs,
        out float fraction)
    {
        // -x -y
        var q1Mins = origMins;
        var q1Maxs = new Vector(origMaxs.X > 0f ? 0f : origMaxs.X, origMaxs.Y > 0f ? 0f : origMaxs.Y, origMaxs.Z);
        if (QuadrantHit(pawn, origin, originBelow, q1Mins, q1Maxs, out normal, out point, out fraction))
        {
            usedMins = q1Mins; usedMaxs = q1Maxs; return true;
        }

        // +x +y
        var q2Mins = new Vector(origMins.X < 0f ? 0f : origMins.X, origMins.Y < 0f ? 0f : origMins.Y, origMins.Z);
        var q2Maxs = origMaxs;
        if (QuadrantHit(pawn, origin, originBelow, q2Mins, q2Maxs, out normal, out point, out fraction))
        {
            usedMins = q2Mins; usedMaxs = q2Maxs; return true;
        }

        // -x +y
        var q3Mins = new Vector(origMins.X, origMins.Y < 0f ? 0f : origMins.Y, origMins.Z);
        var q3Maxs = new Vector(origMaxs.X > 0f ? 0f : origMaxs.X, origMaxs.Y, origMaxs.Z);
        if (QuadrantHit(pawn, origin, originBelow, q3Mins, q3Maxs, out normal, out point, out fraction))
        {
            usedMins = q3Mins; usedMaxs = q3Maxs; return true;
        }

        // +x -y
        var q4Mins = new Vector(origMins.X < 0f ? 0f : origMins.X, origMins.Y, origMins.Z);
        var q4Maxs = new Vector(origMaxs.X, origMaxs.Y > 0f ? 0f : origMaxs.Y, origMaxs.Z);
        if (QuadrantHit(pawn, origin, originBelow, q4Mins, q4Maxs, out normal, out point, out fraction))
        {
            usedMins = q4Mins; usedMaxs = q4Maxs; return true;
        }

        normal = default; point = default; usedMins = origMins; usedMaxs = origMaxs; fraction = 0f;
        return false;
    }

    private bool QuadrantHit(
        IPlayerPawn pawn,
        in Vector from, in Vector to,
        in Vector mins, in Vector maxs,
        out Vector normal, out Vector point, out float fraction)
    {
        var query = RnQueryShapeAttr.PlayerMovement(PlayerSolidLayers);
        query.SetEntityToIgnore(pawn, 0);
        var trace = _physicsQuery.TraceShapePlayerMovement(
            new TraceShapeRay(new TraceShapeHull { Mins = mins, Maxs = maxs }),
            from, to,
            in query);

        if (trace.DidHit() && trace.PlaneNormal.Z >= PhysicsConstants.MinStandableZNrm)
        {
            normal = trace.PlaneNormal;
            point  = trace.EndPosition;
            fraction = trace.Fraction;
            return true;
        }

        normal = default; point = default; fraction = 0f;
        return false;
    }
}
