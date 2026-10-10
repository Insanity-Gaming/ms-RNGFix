/*
 * Source2Surf/Timer
 * Copyright (C) 2025 Nukoooo and Kxnrl
 * Ported to RngFix (Insanity-Gaming/ms-RNGFix) from Source2Surf/Timer @ 063cd772d3e6ac64a5f9261f79c2d51a8ab6973c
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using InsanityGaming.RngFix.Infrastructure;
using Microsoft.Extensions.Logging;
using Sharp.Shared.HookParams;
using Sharp.Shared.Listeners;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace InsanityGaming.RngFix.MovementFix;

// Fixes for stock CS2 movement bugs, one file per fix under MovementFix/.
internal unsafe partial class MovementFixModule : IGameListener, IEntityListener, IEventListener
{
    // rngfix_stairs and rngfix_mpbhops: off, only on maps of the fix's game mode, or on every map.
    private const int ModeOff        = 0;
    private const int ModeOnGameMode = 1;
    private const int ModeAlways     = 2;

    private readonly InterfaceBridge            _bridge;
    private readonly IInlineHookManager         _inlineHookManager;
    private readonly ILogger<MovementFixModule> _logger;

    private static IEntityManager _entityManager = null!;

    // cvars
    // ReSharper disable InconsistentNaming

    private readonly IConVar rngfix_downhill;
    private readonly IConVar rngfix_uphill;
    private readonly IConVar rngfix_telehop;
    private readonly IConVar rngfix_edge;
    private readonly IConVar rngfix_stairs;
    private readonly IConVar rngfix_triggerjump;
    private readonly IConVar rngfix_teleport_keep_angles;
    private readonly IConVar rngfix_mpbhops;

    private readonly IConVar sv_standable_normal;
    private readonly IConVar? sv_stepsize;

    // ReSharper restore InconsistentNaming

    private static bool  _slopefixEnabled;
    private static bool  _uphillEnabled;
    private static bool  _telehopEnabled;
    private static bool  _edgebugEnabled;
    private static int   _stairsMode;
    private static bool  _triggerJumpEnabled;
    private static bool  _keepTeleportAnglesEnabled;
    private static int   _mpbhopsMode;
    private static float _standableNormal;
    private static float _stepSize;

    // CGlobalVars*, set once a map is loaded.
    private static nint _globals;

    // Counts user commands; ProcessMove runs once per sub-tick step.
    private static readonly int[]  _moveTick     = new int[PlayerSlot.MaxPlayerCount];
    private static readonly bool[] _isFakeClient = new bool[PlayerSlot.MaxPlayerCount];

    public MovementFixModule(InterfaceBridge            bridge,
                             IInlineHookManager         inlineHookManager,
                             ILogger<MovementFixModule> logger)
    {
        _bridge            = bridge;
        _inlineHookManager = inlineHookManager;
        _logger            = logger;
        _entityManager     = bridge.EntityManager;

        rngfix_downhill = bridge.ConVarManager.CreateConVar("rngfix_downhill",
                                                            true,
                                                            "Keep the speed a downhill slope gives when landing on it without colliding with it first")
            !;

        rngfix_uphill = bridge.ConVarManager.CreateConVar("rngfix_uphill",
                                                          true,
                                                          "Land players on an uphill slope without the collision that takes away their speed depending on where in the tick they reach it")
            !;

        rngfix_telehop = bridge.ConVarManager.CreateConVar("rngfix_telehop",
                                                           true,
                                                           "Give back the speed a collision or landing took in the same tick a trigger_teleport fires")
            !;

        rngfix_edge = bridge.ConVarManager.CreateConVar("rngfix_edge",
                                                        true,
                                                        "Land players on the edge of a block instead of letting them slide off it depending on where in the tick they hit it")
            !;

        rngfix_stairs = bridge.ConVarManager.CreateConVar("rngfix_stairs",
                                                          ModeOnGameMode,
                                                          ModeOff,
                                                          ModeAlways,
                                                          "Let players slide up stairs at speed: on the ground, and landing on them after hitting a step's face. 0 = off, 1 = surf maps only, 2 = every map")
            !;

        rngfix_triggerjump = bridge.ConVarManager.CreateConVar("rngfix_triggerjump",
                                                               true,
                                                               "Touch the teleports, pushes and trigger_multiples (zones included) lying in the gap between a landing player and the ground")
            !;

        rngfix_teleport_keep_angles = bridge.ConVarManager.CreateConVar("rngfix_teleport_keep_angles",
                                                                        false,
                                                                        "Keep players' view angles and velocity when a trigger_teleport moves them, instead of turning both to the destination")
            !;

        rngfix_mpbhops = bridge.ConVarManager.CreateConVar("rngfix_mpbhops",
                                                           ModeOnGameMode,
                                                           ModeOff,
                                                           ModeAlways,
                                                           "Keep bhop platforms still and teleport or boost each player as the platform would have (from the next map). 0 = off, 1 = bhop maps only, 2 = every map")
            !;

        sv_standable_normal = bridge.ConVarManager.FindConVar("sv_standable_normal")!;
        // Development-only, so only a full search finds it.
        sv_stepsize         = bridge.ConVarManager.FindConVar("sv_stepsize", true);
    }

    public bool Init()
    {
        ResetPlayerState();
        RefreshConVars();

        _bridge.ConVarManager.InstallChangeHook(rngfix_downhill, OnConVarChanged);
        _bridge.ConVarManager.InstallChangeHook(rngfix_uphill, OnConVarChanged);
        _bridge.ConVarManager.InstallChangeHook(rngfix_telehop, OnConVarChanged);
        _bridge.ConVarManager.InstallChangeHook(rngfix_edge, OnConVarChanged);
        _bridge.ConVarManager.InstallChangeHook(rngfix_stairs, OnConVarChanged);
        _bridge.ConVarManager.InstallChangeHook(rngfix_triggerjump, OnConVarChanged);
        _bridge.ConVarManager.InstallChangeHook(rngfix_teleport_keep_angles, OnConVarChanged);
        _bridge.ConVarManager.InstallChangeHook(rngfix_mpbhops, OnConVarChanged);
        _bridge.ConVarManager.InstallChangeHook(sv_standable_normal, OnConVarChanged);

        if (sv_stepsize is not null)
        {
            _bridge.ConVarManager.InstallChangeHook(sv_stepsize, OnConVarChanged);
        }

        InstallHooks();

        _bridge.HookManager.PlayerRunCommand.InstallHookPre(OnPlayerRunCommandPre);
        _bridge.HookManager.PlayerPostThink.InstallForward(OnPlayerPostThink);
        _bridge.ModSharp.InstallGameListener(this);
        _bridge.EntityManager.InstallEntityListener(this);
        _bridge.EventManager.InstallEventListener(this);
        _bridge.EventManager.HookEvent("player_jump");

        return true;
    }

    public void Shutdown()
    {
        _bridge.ModSharp.RemoveGameListener(this);
        _bridge.EntityManager.RemoveEntityListener(this);
        _bridge.EventManager.RemoveEventListener(this);
        _bridge.HookManager.PlayerRunCommand.RemoveHookPre(OnPlayerRunCommandPre);
        _bridge.HookManager.PlayerPostThink.RemoveForward(OnPlayerPostThink);

        _bridge.ConVarManager.RemoveChangeHook(rngfix_downhill, OnConVarChanged);
        _bridge.ConVarManager.RemoveChangeHook(rngfix_uphill, OnConVarChanged);
        _bridge.ConVarManager.RemoveChangeHook(rngfix_telehop, OnConVarChanged);
        _bridge.ConVarManager.RemoveChangeHook(rngfix_edge, OnConVarChanged);
        _bridge.ConVarManager.RemoveChangeHook(rngfix_stairs, OnConVarChanged);
        _bridge.ConVarManager.RemoveChangeHook(rngfix_triggerjump, OnConVarChanged);
        _bridge.ConVarManager.RemoveChangeHook(rngfix_teleport_keep_angles, OnConVarChanged);
        _bridge.ConVarManager.RemoveChangeHook(rngfix_mpbhops, OnConVarChanged);
        _bridge.ConVarManager.RemoveChangeHook(sv_standable_normal, OnConVarChanged);

        if (sv_stepsize is not null)
        {
            _bridge.ConVarManager.RemoveChangeHook(sv_stepsize, OnConVarChanged);
        }

        // RngFixModule removes the detours before this runs.
        if (_triggerFilterVtable != null)
        {
            FreeTriggerFilterVtable();
        }

        _globals    = nint.Zero;
        _globalVars = null;
        GameMode.Reset();
    }

    public void OnGameInit()
    {
        _globalVars = _bridge.ModSharp.GetGlobals();
        _globals    = _globalVars.GetAbsPtr();
        GameMode.Refresh(_globalVars.MapName);
        ResetBhopBlocks();
    }

    public void OnGameShutdown()
    {
        _globals    = nint.Zero;
        _globalVars = null;
        GameMode.Reset();
        ResetBhopBlocks();
    }

    public int ListenerVersion  => IGameListener.ApiVersion;
    public int ListenerPriority => 0;

    // Jumping off a rising bhop platform; the only event hooked.
    public void FireGameEvent(IGameEvent e)
    {
        if (e.Name == "player_jump")
        {
            OnBhopBlockJump(e);
        }
    }

    private void OnConVarChanged(IConVar conVar)
        => RefreshConVars();

    private void RefreshConVars()
    {
        _slopefixEnabled           = rngfix_downhill.GetBool();
        _uphillEnabled             = rngfix_uphill.GetBool();
        _telehopEnabled            = rngfix_telehop.GetBool();
        _edgebugEnabled            = rngfix_edge.GetBool();
        _stairsMode                = rngfix_stairs.GetInt32();
        _triggerJumpEnabled        = rngfix_triggerjump.GetBool();
        _keepTeleportAnglesEnabled = rngfix_teleport_keep_angles.GetBool();
        _mpbhopsMode               = rngfix_mpbhops.GetInt32();
        _standableNormal           = sv_standable_normal.GetFloat();
        _stepSize                  = sv_stepsize?.GetFloat() ?? 18.0f;
    }

    private static bool IsModeActive(int mode, EGameMode gameMode)
        => mode == ModeAlways || (mode == ModeOnGameMode && GameMode.Current == gameMode);

    private static bool IsStairsActive()
        => IsModeActive(_stairsMode, EGameMode.Surf);

    private static bool IsMpbhopsActive()
        => IsModeActive(_mpbhopsMode, EGameMode.Bhop);

    private static void ResetPlayerState()
    {
        Array.Fill(_moveTick, 0);
        Array.Fill(_isFakeClient, false);
        Array.Fill(_moved, false);
        Array.Fill(_collided, false);
        Array.Fill(_speedLossTick, int.MinValue);
        Array.Fill(_teleportTick, int.MinValue);
        Array.Fill(_landTick, int.MinValue);
        Array.Fill(_touchingCount, 0);
        ResetBhopBlocks();
    }

    private static HookReturnValue<EmptyHookReturn> OnPlayerRunCommandPre(IPlayerRunCommandHookParams      @params,
                                                                          HookReturnValue<EmptyHookReturn> ret)
    {
        var client = @params.Client;
        int slot   = client.Slot;

        _moveTick[slot]++;
        _isFakeClient[slot] = client.IsFakeClient;
        _moved[slot]        = false;
        _collided[slot]     = false;

        return new ();
    }
}
