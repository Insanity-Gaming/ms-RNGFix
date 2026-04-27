using InsanityGaming.RngFix.Models;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Types;

namespace InsanityGaming.RngFix.Services;

public interface IPhysicsSimulator
{
    /// <summary>
    /// Simulates duck state. Adjusts nextOrigin and sets mins/maxs for the player hull.
    /// </summary>
    void SimulateDuck(ModulePlayerState state, IPlayerPawn pawn, ref Vector nextOrigin, out Vector mins, out Vector maxs);

    /// <summary>
    /// Applies first half-tick of gravity to velocity (modifies in place).
    /// </summary>
    void StartGravity(ModulePlayerState state, IPlayerPawn pawn, ref Vector velocity);

    /// <summary>
    /// Applies second half-tick of gravity to velocity (modifies in place).
    /// </summary>
    void FinishGravity(ModulePlayerState state, IPlayerPawn pawn, ref Vector velocity);

    /// <summary>
    /// Simulates jump button press. Modifies velocity if player can and wants to jump.
    /// Also calls FinishGravity internally (matches engine behavior).
    /// </summary>
    void CheckJumpButton(ModulePlayerState state, IPlayerPawn pawn, ref Vector velocity);

    /// <summary>
    /// Simulates air acceleration. maxSpeed comes from CMoveData.MaxSpeed.
    /// </summary>
    void AirAccelerate(ModulePlayerState state, ref Vector velocity, float maxSpeed);

    /// <summary>
    /// Clamps velocity to sv_maxvelocity on all axes.
    /// </summary>
    void CheckVelocity(ref Vector velocity);

    /// <summary>
    /// Reflects velocity off a surface normal (no overbounce — walkable surfaces only).
    /// </summary>
    void ClipVelocity(in Vector velocity, in Vector normal, out Vector result);

    /// <summary>
    /// Returns jump impulse from sv_jump_impulse if available, else DefaultJumpImpulse.
    /// </summary>
    float GetJumpImpulse();

    /// <summary>
    /// Returns true if player is more than knee-deep in water.
    /// </summary>
    bool IsInWater(IPlayerPawn pawn);
}
