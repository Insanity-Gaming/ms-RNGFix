using InsanityGaming.RngFix.Config;
using InsanityGaming.RngFix.Fixes;
using InsanityGaming.RngFix.Handlers;
using InsanityGaming.RngFix.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Definition;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.HookParams;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;

namespace InsanityGaming.RngFix;

/// <summary>
/// RNGFix ModSharp module — composition root.
/// Builds the internal DI container in <see cref="Init"/> and wires lifecycle via
/// <see cref="IRngModule"/> iteration. Does not contain fix logic itself.
/// </summary>
public sealed class RngFixModule : IModSharpModule, IClientListener, IEntityListener
{
    string IModSharpModule.DisplayName => "RngFix";
    string IModSharpModule.DisplayAuthor => "Retro";

    private readonly ISharedSystem _sharedSystem;
    private readonly ILogger<RngFixModule> _logger;

    private IServiceProvider _serviceProvider = null!;
    private IPlayerStateService _playerStateService = null!;
    private ITriggerTracker _triggerTracker = null!;
    private TriggerNatives _triggerNatives = null!;
    private readonly HashSet<int> _teleportTriggers = new();
    private readonly Dictionary<(int TriggerIdx, int PlayerIdx), int> _lastTeleportTouchTick = new();

    // ──────────────────────────────── Constructor ────────────────────────────────

    public RngFixModule(
        ISharedSystem sharedSystem,
        string dllPath,
        string sharpPath,
        Version version,
        IConfiguration configuration,
        bool hotReload)
    {
        _sharedSystem = sharedSystem;
        _logger       = sharedSystem.GetLoggerFactory().CreateLogger<RngFixModule>();
    }

    // ──────────────────────────────── IModSharpModule lifecycle ────────────────────────────────

    public bool Init()
    {
        // ── Build the internal DI container ──
        var services = new ServiceCollection();

        // Framework services
        services.AddSingleton(_sharedSystem);
        services.AddSingleton(_sharedSystem.GetConVarManager());
        services.AddSingleton(_sharedSystem.GetPhysicsQueryManager());
        services.AddSingleton(_sharedSystem.GetEntityManager());
        services.AddSingleton<ILoggerFactory>(_sharedSystem.GetLoggerFactory());
        services.AddLogging();

        // Core services
        services.AddSingleton<RngFixConVars>();
        services.AddSingleton<IPlayerStateService, PlayerStateService>();
        services.AddSingleton<ITriggerTracker, TriggerTracker>();
        services.AddSingleton<ITriggerTouchSynthesizer, TriggerTouchSynthesizer>();
        services.AddSingleton<IPhysicsSimulator, PhysicsSimulator>();
        services.AddSingleton<TriggerNatives>();

        // Fix classes
        services.AddSingleton<EdgeBugFix>();
        services.AddSingleton<InclineFix>();
        services.AddSingleton<TriggerJumpFix>();
        services.AddSingleton<StairsFix>();
        services.AddSingleton<TelehopFix>();

        // Handlers — registered as concrete types AND as IRngModule for lifecycle iteration
        services.AddSingleton<MovementPreHandler>();
        services.AddSingleton<PostThinkHandler>();
        services.AddSingleton<IRngModule>(sp => sp.GetRequiredService<MovementPreHandler>());
        services.AddSingleton<IRngModule>(sp => sp.GetRequiredService<PostThinkHandler>());

        _serviceProvider    = services.BuildServiceProvider();
        _playerStateService = _serviceProvider.GetRequiredService<IPlayerStateService>();
        _triggerTracker     = _serviceProvider.GetRequiredService<ITriggerTracker>();
        _triggerNatives     = _serviceProvider.GetRequiredService<TriggerNatives>();

        // ── Install listeners ──
        _sharedSystem.GetClientManager().InstallClientListener(this);
        _sharedSystem.GetEntityManager().InstallEntityListener(this);
        _sharedSystem.GetModSharp().InstallGameFrameHook(null, OnGameFramePost);
        // ── Initialize sub-modules ──
        foreach (IRngModule service in _serviceProvider.GetServices<IRngModule>())
        {
            if (!service.Init())
                _logger.LogError("RngFix sub-module {Name} failed to initialize.", service.GetType().Name);
        }

        _logger.LogInformation("RngFix initialized.");
        return true;
    }

    public void Shutdown()
    {
        foreach (IRngModule service in _serviceProvider.GetServices<IRngModule>())
            service.Shutdown();

        _sharedSystem.GetClientManager().RemoveClientListener(this);
        _sharedSystem.GetEntityManager().RemoveEntityListener(this);
        _logger.LogInformation("RngFix shut down.");
    }

    // ──────────────────────────────── IClientListener ────────────────────────────────

    int IClientListener.ListenerVersion  => IClientListener.ApiVersion;
    int IClientListener.ListenerPriority => 0;

    public void OnClientConnected(IGameClient client)
    {
        int idx = GetPlayerIndex(client);
        if (idx != -1) _playerStateService.Reset(idx);
    }

    public void OnClientDisconnected(IGameClient client, NetworkDisconnectionReason reason)
    {
        int idx = GetPlayerIndex(client);
        if (idx != -1)
        {
            _serviceProvider.GetRequiredService<ITriggerTouchSynthesizer>().CleanupPlayer(idx);
            _playerStateService.Remove(idx);
        }
    }

    // ──────────────────────────────── IEntityListener ────────────────────────────────

    int IEntityListener.ListenerVersion  => IEntityListener.ApiVersion;
    int IEntityListener.ListenerPriority => 0;

    public void OnEntityCreated(IBaseEntity entity)
    {
        if (!entity.Classname.StartsWith("trigger_", StringComparison.OrdinalIgnoreCase)) return;

        _sharedSystem.GetEntityManager().HookEntityOutput(entity.Classname, "OnStartTouch");
        _sharedSystem.GetEntityManager().HookEntityOutput(entity.Classname, "OnEndTouch");

        if (entity.Classname.Equals("trigger_teleport", StringComparison.OrdinalIgnoreCase))
            _teleportTriggers.Add(entity.Index);
    }

    public void OnEntityDeleted(IBaseEntity entity)
    {
        _teleportTriggers.Remove(entity.Index);
        _serviceProvider.GetRequiredService<ITriggerTouchSynthesizer>().CleanupTrigger(entity.Index);
    }

    public EHookAction OnEntityFireOutput(IBaseEntity entity, string output, IBaseEntity? activator, float delay)
    {
        if (!entity.Classname.StartsWith("trigger_", StringComparison.OrdinalIgnoreCase))
            return EHookAction.Ignored;

        if (activator?.AsPlayerPawn() is not { } pawn)
            return EHookAction.Ignored;
        
        var trigger = entity.As<IBaseTrigger>();
        if (!trigger.IsValid()) return EHookAction.Ignored;

        int playerIdx  = pawn.Index;
        int triggerIdx = entity.Index;
        bool isTeleportTrigger = _teleportTriggers.Contains(triggerIdx);

        if (output.Equals("OnStartTouch", StringComparison.OrdinalIgnoreCase))
        {
            _triggerTracker.SetTouching(playerIdx, triggerIdx, true);
            if (isTeleportTrigger)
            {
                var state = _playerStateService.Get(playerIdx);
                if (state is not null) state.TouchingTeleportTriggerCount++;
            }
        }
        else if (output.Equals("OnEndTouch", StringComparison.OrdinalIgnoreCase))
        {
            _triggerTracker.SetTouching(playerIdx, triggerIdx, false);
            _serviceProvider.GetRequiredService<ITriggerTouchSynthesizer>().CleanupSyntheticTouch(entity, pawn);
            if (isTeleportTrigger)
            {
                var state = _playerStateService.Get(playerIdx);
                if (state is not null && state.TouchingTeleportTriggerCount > 0)
                    state.TouchingTeleportTriggerCount--;
            }
        }

        return EHookAction.Ignored;
    }

    // ──────────────────────────────── Helpers ────────────────────────────────

    private unsafe void OnGameFramePost(bool b, bool b1, bool arg3)
    {
        if (_teleportTriggers.Count == 0)
            return;

        List<int>? invalidTriggers = null;

        foreach (int triggerIdx in _teleportTriggers)
        {
            IBaseEntity? entity;

            try
            {
                entity = _sharedSystem.GetEntityManager().FindEntityByIndex(triggerIdx);
            }
            catch
            {
                entity = null;
            }

            if (entity is null || !entity.IsValid() ||
                !entity.Classname.Equals("trigger_teleport", StringComparison.OrdinalIgnoreCase))
            {
                invalidTriggers ??= new List<int>();
                invalidTriggers.Add(triggerIdx);
                continue;
            }

            var trigger = entity.As<IBaseTrigger>();
            if (!trigger.IsValid() || !HasValidTeleportTarget(trigger))
                continue;

            var touching = trigger.GetTouchingEntities().GetUtlVector();
            int count = touching->Count;

            for (int i = 0; i < count; i++)
            {
                var handle = touching->Element(i);
                var entityIndex = handle.GetEntryIndex();
                if (entityIndex <= 0)
                    continue;

                IBaseEntity? other;
                try
                {
                    other = _sharedSystem.GetEntityManager().FindEntityByIndex(entityIndex);
                }
                catch
                {
                    continue;
                }

                if (other?.AsPlayerPawn() is not { } pawn || !pawn.IsValid())
                    continue;

                var state = _playerStateService.Get(pawn.Index);
                if (state is null)
                    continue;

                var key = (triggerIdx, pawn.Index);
                if (_lastTeleportTouchTick.TryGetValue(key, out int lastTick) && lastTick == state.Tick)
                    continue;

                if (!_triggerNatives.PassesTriggerFilters(entity, pawn))
                    continue;

                _lastTeleportTouchTick[key] = state.Tick;

                if (state.LastMapTeleportTick == state.Tick - 1)
                    state.MapTeleportedSequentialTicks = true;

                state.LastMapTeleportTick = state.Tick;
            }
        }

        if (invalidTriggers is null)
            return;

        foreach (int triggerIdx in invalidTriggers)
            _teleportTriggers.Remove(triggerIdx);
    }

    private bool HasValidTeleportTarget(IBaseTrigger trigger)
    {
        var targetName = trigger.GetNetVarUtlSymbolLarge("m_target");
        if (string.IsNullOrWhiteSpace(targetName))
            return false;

        return TargetNameExists(targetName);
    }

    private bool TargetNameExists(string targetName)
    {
        if (targetName[0] == '!')
            return true;

        var entity =  _sharedSystem.GetEntityManager().FindEntityByName(null, targetName);
        if (entity is null || !entity.IsValid())
            return false;

        string? name = entity.Name;
        if (string.Equals(name, targetName, StringComparison.Ordinal))
            return true;

        return false;
    }

    private static int GetPlayerIndex(IGameClient client)
        => client.GetPlayerController()?.GetPlayerPawn()?.Index ?? -1;
}
