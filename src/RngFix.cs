using System.Runtime.CompilerServices;
using InsanityGaming.RngFix.Infrastructure;
using InsanityGaming.RngFix.MovementFix;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Objects;

// The movement fix detours take and return bool across the native boundary ([UnmanagedCallersOnly]), which is only
// blittable (as a 1-byte C++ bool) with runtime marshalling disabled, as in Source2Surf/Timer.
[assembly: DisableRuntimeMarshalling]

namespace InsanityGaming.RngFix;

/// <summary>
/// RNGFix ModSharp module — composition root for the movement fixes ported from Source2Surf/Timer's
/// MovementFixModule. Holds no fix logic itself.
/// </summary>
public sealed class RngFixModule : IModSharpModule
{
    string IModSharpModule.DisplayName   => "RngFix";
    string IModSharpModule.DisplayAuthor => "Retro";

    private const string GameDataName = "rngfix.games";

    private readonly ISharedSystem         _sharedSystem;
    private readonly ILogger<RngFixModule> _logger;

    private ServiceProvider?   _serviceProvider;
    private IGameData?         _gameData;
    private InlineHookManager? _inlineHookManager;
    private MovementFixModule? _movementFix;

    public RngFixModule(ISharedSystem  sharedSystem,
                        string         dllPath,
                        string         sharpPath,
                        Version        version,
                        IConfiguration configuration,
                        bool           hotReload)
    {
        _sharedSystem = sharedSystem;
        _logger       = sharedSystem.GetLoggerFactory().CreateLogger<RngFixModule>();
    }

    public bool Init()
    {
        // The signatures backing the CategorizePosition and TryPlayerMove scans; needed before the hooks go in.
        _gameData = _sharedSystem.GetModSharp().GetGameData();
        _gameData.Register(GameDataName);

        var services = new ServiceCollection();

        services.AddSingleton(_sharedSystem);
        services.AddSingleton<ILoggerFactory>(_sharedSystem.GetLoggerFactory());
        services.AddLogging();
        services.AddSingleton<InterfaceBridge>();
        services.AddSingleton<InlineHookManager>();
        services.AddSingleton<IInlineHookManager>(sp => sp.GetRequiredService<InlineHookManager>());
        services.AddSingleton<MovementFixModule>();

        _serviceProvider   = services.BuildServiceProvider();
        _inlineHookManager = _serviceProvider.GetRequiredService<InlineHookManager>();
        _movementFix       = _serviceProvider.GetRequiredService<MovementFixModule>();

        _movementFix.Init();

        _logger.LogInformation("RngFix loaded");

        return true;
    }

    public void Shutdown()
    {
        // Detours first, so no hook runs into a half torn down module (Timer's shutdown order).
        _inlineHookManager?.Shutdown();
        _movementFix?.Shutdown();

        _serviceProvider?.Dispose();
        _serviceProvider   = null;
        _inlineHookManager = null;
        _movementFix       = null;

        _gameData?.Unregister(GameDataName);
        _gameData = null;
    }
}
