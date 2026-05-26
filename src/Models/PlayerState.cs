using Sharp.Shared.Enums;
using Sharp.Shared.Types;

namespace InsanityGaming.RngFix.Models;

public sealed class PlayerState
{
    public int Tick { get; set; }
    public float FrameTime { get; set; }

    // Pre-tick inputs from CMoveData
    public UserCommandButtons Buttons { get; set; }
    public UserCommandButtons ChangedButtons { get; set; }
    public float ForwardMove { get; set; }
    public float SideMove { get; set; }
    public float AnglePitch { get; set; }
    public float AngleYaw { get; set; }

    // Collision prediction context
    public int LastTickPredicted { get; set; }
    public Vector PreCollisionVelocity { get; set; }
    public Vector LastBaseVelocity { get; set; }

    // Ground tracking
    public bool WasInAirPreTick { get; set; }
    public int PreTickGroundEnt { get; set; } = -1;
    public int LastLandTick { get; set; }

    // Collision data
    public int LastCollisionTick { get; set; }
    public Vector CollisionPoint { get; set; }
    public Vector CollisionNormal { get; set; }

    // Teleport tracking
    public int LastMapTeleportTick { get; set; }
    public bool MapTeleportedSequentialTicks { get; set; }

    // Trigger touch tracking (get-only HashSet, never replaced)
    public HashSet<int> TouchingTriggers { get; } = new();

    // Count of trigger_teleport entities currently touching this player
    public int TouchingTeleportTriggerCount { get; set; }

    // Per-player hull cache — populated lazily by PhysicsSimulator.SimulateDuck
    public Vector? HullMins { get; set; }
    public Vector? HullMaxsUnducked { get; set; }
    public Vector? HullMaxsDucked { get; set; }
    public float?  DuckDelta { get; set; }

    public void Reset()
    {
        Tick = 0;
        FrameTime = 0f;

        Buttons = 0UL;
        ChangedButtons = 0UL;
        ForwardMove = 0f;
        SideMove = 0f;
        AnglePitch = 0f;
        AngleYaw = 0f;

        LastTickPredicted = 0;
        PreCollisionVelocity = default;
        LastBaseVelocity = default;

        WasInAirPreTick = false;
        PreTickGroundEnt = -1;
        LastLandTick = 0;

        LastCollisionTick = 0;
        CollisionPoint = default;
        CollisionNormal = default;

        LastMapTeleportTick = 0;
        MapTeleportedSequentialTicks = false;

        TouchingTriggers.Clear();
        TouchingTeleportTriggerCount = 0;

        HullMins = null;
        HullMaxsUnducked = null;
        HullMaxsDucked = null;
        DuckDelta = null;
    }
}
