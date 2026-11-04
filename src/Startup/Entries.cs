using ValheimJS.Scripting;

namespace ValheimJS.Startup;

internal interface IStartupModule
{
    void Start(Plugin plugin);
    void Stop(Plugin plugin);
}

[Shared]
internal sealed class SharedEntry : IStartupModule
{
    public void Start(Plugin plugin) => plugin.StartShared();
    public void Stop(Plugin plugin) => plugin.StopShared();
}

[ClientOnly]
internal sealed class ClientEntry : IStartupModule
{
    // ChatService is deliberately dormant. Its implementation is retained without registration.
    public void Start(Plugin plugin) { }
    public void Stop(Plugin plugin)
    {
#if USE_INPUT
        Input.KeyboardManager.Clear();
#endif
    }
}

[ServerOnly]
internal sealed class ServerEntry : IStartupModule
{
    public void Start(Plugin plugin) => Plugin.Logger.LogInfo("Dedicated-server startup module initialized.");
    public void Stop(Plugin plugin) { }
}
