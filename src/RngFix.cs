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
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;

namespace InsanityGaming.RngFix;

/// <summary>
/// RNGFix ModSharp module — composition root.
/// Builds the internal DI container in <see cref="Init"/> and wires lifecycle via
/// <see cref="IRngModule"/> iteration. Does not contain fix logic itself.
/// </summary>
public sealed class RngFixModule : IModSharpModule, IClientListener, IEntityListener, IGameListener
{
    string IModSharpModule.DisplayName => "RngFix";
    string IModSharpModule.DisplayAuthor => "Retro";

    private readonly ISharedSystem _sharedSystem;
    private readonly ILogger<RngFixModule> _logger;

    private IServiceProvider _serviceProvider = null!;
    private RngFixConVars _conVars = null!;
    private IPlayerStateService _playerStateService = null!;
    private ITriggerTracker _triggerTracker = null!;
    private TriggerNatives _triggerNatives = null!;
    private IEntityManager _entityManager = null!;

    // Dense list for cheap, re-entrancy-safe iteration every frame (indexed for-loop, not
    // foreach — a foreach over a mutated collection throws; an indexed loop that re-checks
    // Count each iteration just processes fewer/more elements, never crashes).
    private readonly List<int> _teleportTriggerIndices = new();
    // Parallel set for O(1) "is this a teleport trigger" checks (OnEntityFireOutput).
    private readonly HashSet<int> _teleportTriggerSet = new();
    // Cached m_target validity per trigger index — resolved lazily (and at most once it comes
    // back true) instead of via FindEntityByName every frame. See HasValidTeleportTarget.
    private readonly Dictionary<int, bool> _teleportTargetValid = new();

    private readonly HashSet<string> _hookedOutputClassnames = new();

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
        _conVars            = _serviceProvider.GetRequiredService<RngFixConVars>();
        _playerStateService = _serviceProvider.GetRequiredService<IPlayerStateService>();
        _triggerTracker     = _serviceProvider.GetRequiredService<ITriggerTracker>();
        _triggerNatives     = _serviceProvider.GetRequiredService<TriggerNatives>();
        _entityManager      = _serviceProvider.GetRequiredService<IEntityManager>();

        // ── Install listeners ──
        _sharedSystem.GetClientManager().InstallClientListener(this);
        _sharedSystem.GetEntityManager().InstallEntityListener(this);
        _sharedSystem.GetModSharp().InstallGameListener(this);
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
        _sharedSystem.GetModSharp().RemoveGameListener(this);
        _sharedSystem.GetModSharp().RemoveGameFrameHook(null, OnGameFramePost);

        if (_serviceProvider is IDisposable disposableProvider)
            disposableProvider.Dispose();

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
            _playerStateService.Remove(idx);
    }

    // ──────────────────────────────── IEntityListener ────────────────────────────────

    int IEntityListener.ListenerVersion  => IEntityListener.ApiVersion;
    int IEntityListener.ListenerPriority => 0;

    public void OnEntityCreated(IBaseEntity entity)
    {
        // Classname must not be read before validity is confirmed — see IBaseEntity.Classname docs.
        if (!entity.IsValid())
            return;

        if (!entity.Classname.StartsWith("trigger_", StringComparison.OrdinalIgnoreCase)) return;

        if (_hookedOutputClassnames.Add(entity.Classname))
        {
            _sharedSystem.GetEntityManager().HookEntityOutput(entity.Classname, "OnStartTouch");
            _sharedSystem.GetEntityManager().HookEntityOutput(entity.Classname, "OnEndTouch");
        }

        if (entity.Classname.Equals("trigger_teleport", StringComparison.OrdinalIgnoreCase))
        {
            if (_teleportTriggerSet.Add(entity.Index))
                _teleportTriggerIndices.Add(entity.Index);
        }
    }

    public void OnEntityDeleted(IBaseEntity entity)
    {
        int entityIdx = entity.Index;

        if (_teleportTriggerSet.Remove(entityIdx))
            _teleportTriggerIndices.Remove(entityIdx);
        _teleportTargetValid.Remove(entityIdx);
    }

    public EHookAction OnEntityFireOutput(IBaseEntity entity, string output, IBaseEntity? activator, float delay)
    {
        // Classname must not be read before validity is confirmed — see IBaseEntity.Classname docs.
        if (!entity.IsValid())
            return EHookAction.Ignored;

        if (!entity.Classname.StartsWith("trigger_", StringComparison.OrdinalIgnoreCase))
            return EHookAction.Ignored;

        if (activator?.AsPlayerPawn() is not { } pawn)
            return EHookAction.Ignored;

        var trigger = entity.As<IBaseTrigger>();
        if (!trigger.IsValid()) return EHookAction.Ignored;

        int playerIdx  = pawn.Index;
        int triggerIdx = entity.Index;
        bool isTeleportTrigger = _teleportTriggerSet.Contains(triggerIdx);

        if (_conVars.IsTouchTrackingEnabled)
        {
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
                if (isTeleportTrigger)
                {
                    var state = _playerStateService.Get(playerIdx);
                    if (state is not null && state.TouchingTeleportTriggerCount > 0)
                        state.TouchingTeleportTriggerCount--;
                }
            }
        }

        return EHookAction.Ignored;
    }

    // ──────────────────────────────── IGameListener ────────────────────────────────

    int IGameListener.ListenerVersion  => IGameListener.ApiVersion;
    int IGameListener.ListenerPriority => 0;

    public void OnGameDeactivate()
    {
        // Hooks registered via HookEntityOutput have no unhook API (IEntityManager exposes
        // HookEntityOutput/HookEntityInput only) — treat a registration as permanent and never
        // clear this guard, otherwise every map change re-registers the same (classname, output)
        // pair, and each duplicate registration fires the handler again per touch.
        // (Guard intentionally NOT cleared here.)

        // Teleport-trigger tracking is per-map — indices are reused by the next map's entities.
        _teleportTriggerIndices.Clear();
        _teleportTriggerSet.Clear();
        _teleportTargetValid.Clear();

        // Player state is keyed by pawn entity index, which the next map's entities reuse.
        // Without this, a fresh player could silently inherit a previous occupant's state
        // (e.g. a stuck TouchingTeleportTriggerCount permanently disabling EdgeBugFix/InclineFix).
        _playerStateService.Clear();
    }

    public void OnServerInit()       { }
    public void OnServerSpawn()      { }
    public void OnServerActivate()   { }
    public void OnResourcePrecache() { }
    public void OnGameInit()         { }
    public void OnGamePostInit()     { }
    public void OnGameActivate()     { }
    public void OnGamePreShutdown()  { }
    public void OnGameShutdown()     { }
    public void OnRoundRestart()     { }
    public void OnRoundRestarted()   { }
    public ECommandAction ConsoleSay(string message) => ECommandAction.Skipped;

    // ──────────────────────────────── Helpers ────────────────────────────────

    private unsafe void OnGameFramePost(bool b, bool b1, bool arg3)
    {
        // Indexed for-loop, not foreach: Count is re-checked every iteration, so a re-entrant
        // add/remove triggered from PassesTriggerFilters (below) during this loop just changes
        // how many entries get processed this frame — it can never throw or read past the end,
        // unlike foreach's enumerator, which throws on any collection mutation mid-iteration.
        for (int t = 0; t < _teleportTriggerIndices.Count; t++)
        {
            int triggerIdx = _teleportTriggerIndices[t];

            IBaseEntity? entity;

            try
            {
                entity = _entityManager.FindEntityByIndex(triggerIdx);
            }
            catch
            {
                entity = null;
            }

            if (entity is null || !entity.IsValid() ||
                !entity.Classname.Equals("trigger_teleport", StringComparison.OrdinalIgnoreCase))
            {
                if (_teleportTriggerSet.Remove(triggerIdx))
                    _teleportTriggerIndices.RemoveAt(t--);
                _teleportTargetValid.Remove(triggerIdx);
                continue;
            }

            var trigger = entity.As<IBaseTrigger>();
            if (!trigger.IsValid()) continue;

            var touching = trigger.GetTouchingEntities().GetUtlVector();

            // Nobody touching this trigger — skip the (cached, but still a dictionary hit)
            // target-validity check entirely. Cheapest possible path for the common case.
            if (touching->Count == 0)
                continue;

            if (!_teleportTargetValid.TryGetValue(triggerIdx, out bool targetValid) || !targetValid)
            {
                // Resolved once and cached permanently once true. If still false (e.g. the
                // trigger's target hasn't spawned yet), this re-checks every frame until it
                // resolves — same cost as before caching, but only while unresolved.
                targetValid = HasValidTeleportTarget(trigger);
                _teleportTargetValid[triggerIdx] = targetValid;
                if (!targetValid) continue;
            }

            // Re-read Count every iteration: PassesTriggerFilters below is a real native vcall
            // that can run entity I/O and mutate this same touch list mid-loop.
            for (int i = 0; i < touching->Count; i++)
            {
                var handle = touching->Element(i);
                var entityIndex = handle.GetEntryIndex();
                if (entityIndex <= 0)
                    continue;

                IBaseEntity? other;
                try
                {
                    other = _entityManager.FindEntityByIndex(entityIndex);
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

                // Per-player-per-tick dedupe: this loop runs every frame, and multiple frames
                // can occur within a single game tick, so skip re-processing this player once
                // they've already been marked teleported this tick (by this trigger or another).
                if (state.LastMapTeleportTick == state.Tick)
                    continue;

                if (!_triggerNatives.PassesTriggerFilters(entity, pawn))
                    continue;

                if (state.LastMapTeleportTick == state.Tick - 1)
                    state.MapTeleportedSequentialTicks = true;

                state.LastMapTeleportTick = state.Tick;
            }
        }
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

        var entity = _entityManager.FindEntityByName(null, targetName);
        if (entity is null || !entity.IsValid())
            return false;

        string? name = entity.Name;
        if (string.Equals(name, targetName, StringComparison.Ordinal))
            return true;

        return false;
    }

    /// <summary>
    /// Resolves the client's current pawn entity index via its stable engine slot, rather than
    /// walking Controller -> Pawn (which returns null once either has already been torn down,
    /// e.g. at disconnect) — this lets disconnect-time cleanup run even when the pawn is gone.
    /// </summary>
    private int GetPlayerIndex(IGameClient client)
        => _entityManager.FindPlayerPawnBySlot(client.Slot)?.Index ?? -1;
}
