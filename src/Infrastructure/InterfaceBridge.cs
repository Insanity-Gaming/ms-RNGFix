using Sharp.Shared;
using Sharp.Shared.Managers;

namespace InsanityGaming.RngFix.Infrastructure;

// The ModSharp managers the movement fixes use, under the names Timer's InterfaceBridge gives them.
internal sealed class InterfaceBridge
{
    public InterfaceBridge(ISharedSystem sharedSystem)
    {
        ModSharp      = sharedSystem.GetModSharp();
        Modules       = sharedSystem.GetLibraryModuleManager();
        ConVarManager = sharedSystem.GetConVarManager();
        EventManager  = sharedSystem.GetEventManager();
        EntityManager = sharedSystem.GetEntityManager();
        HookManager   = sharedSystem.GetHookManager();
        SchemaManager = sharedSystem.GetSchemaManager();
    }

    public IModSharp             ModSharp { get; }
    public ILibraryModuleManager Modules  { get; }

    public IConVarManager ConVarManager { get; }
    public IEventManager  EventManager  { get; }
    public IEntityManager EntityManager { get; }
    public IHookManager   HookManager   { get; }
    public ISchemaManager SchemaManager { get; }
}
