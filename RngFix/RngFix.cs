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

        // ── Install listeners ──
        _sharedSystem.GetClientManager().InstallClientListener(this);
        _sharedSystem.GetEntityManager().InstallEntityListener(this);

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
        if (idx != -1) _playerStateService.Remove(idx);
    }

    // ──────────────────────────────── IEntityListener ────────────────────────────────

    int IEntityListener.ListenerVersion  => IEntityListener.ApiVersion;
    int IEntityListener.ListenerPriority => 0;

    public void OnEntityCreated(IBaseEntity entity)
    {
        if (!entity.Classname.StartsWith("trigger_", StringComparison.OrdinalIgnoreCase)) return;

        _sharedSystem.GetEntityManager().HookEntityOutput(entity.Classname, "OnStartTouch");
        _sharedSystem.GetEntityManager().HookEntityOutput(entity.Classname, "OnEndTouch");
    }

    public EHookAction OnEntityFireOutput(IBaseEntity entity, string output, IBaseEntity? activator, float delay)
    {
        if (!entity.Classname.StartsWith("trigger_", StringComparison.OrdinalIgnoreCase))
            return EHookAction.Ignored;

        if (activator?.AsPlayerPawn() is not { } pawn)
            return EHookAction.Ignored;

        int playerIdx  = pawn.Index;
        int triggerIdx = entity.Index;

        if (output.Equals("OnStartTouch", StringComparison.OrdinalIgnoreCase))
        {
            _triggerTracker.SetTouching(playerIdx, triggerIdx, true);

            if (entity.Classname.Equals("trigger_teleport", StringComparison.OrdinalIgnoreCase))
            {
                var state = _playerStateService.Get(playerIdx);
                if (state is not null)
                {
                    if (state.LastMapTeleportTick == state.Tick - 1)
                        state.MapTeleportedSequentialTicks = true;

                    state.LastMapTeleportTick = state.Tick;
                }
            }
        }
        else if (output.Equals("OnEndTouch", StringComparison.OrdinalIgnoreCase))
        {
            _triggerTracker.SetTouching(playerIdx, triggerIdx, false);
        }

        return EHookAction.Ignored;
    }

    // ──────────────────────────────── Helpers ────────────────────────────────

    private static int GetPlayerIndex(IGameClient client)
        => client.GetPlayerController()?.GetPlayerPawn()?.Index ?? -1;
}
