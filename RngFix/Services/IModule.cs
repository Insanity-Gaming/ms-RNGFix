namespace InsanityGaming.RngFix.Services;

/// <summary>
/// Lifecycle interface for RngFix sub-modules. The main <see cref="RngFixModule"/> iterates
/// all registered <c>IRngModule</c> services in <c>Init()</c> and <c>Shutdown()</c>.
/// </summary>
public interface IRngModule
{
    bool Init();
    void Shutdown();
}
