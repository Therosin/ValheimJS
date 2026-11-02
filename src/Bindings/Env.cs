using System;
using ValheimJS.Scripting.Binding;
using UnityEngine;

namespace ValheimJS.Bindings;

/// <summary>
/// Which side of the game this process is. Scripts see <c>host.api.env.*</c>.
/// <para>
/// <c>dedicated</c> is decided once at startup (a headless process); <c>isServer</c> also becomes
/// true later when a player hosts a world, so prefer it for authority checks.
/// </para>
/// </summary>
[JsBinding("Env", JsBindPhase.Core, order: 10)]
internal static class EnvBindings
{
    public static void Bind(JsBindingContext context)
    {
        context.Add("env.dedicated", new Func<bool>(Dedicated));
        context.Add("env.isServer", new Func<bool>(IsServer));
        context.Add("env.isClient", new Func<bool>(() => !Dedicated()));
        context.Add("env.side", new Func<string>(() => Dedicated() ? "server" : "client"));
        context.Add("env.singlePlayer", new Func<bool>(() => ZNet.IsSinglePlayer));
        context.Add("env.peers", new Func<int>(() => ZNet.instance?.GetPeers()?.Count ?? 0));
        context.Add("env.instance", new Func<string>(() => InstanceId));
    }

    private static readonly string InstanceId = Guid.NewGuid().ToString("N");

    /// <summary>Headless process: true for a dedicated server, false for any windowed client.</summary>
    internal static bool Dedicated() => Application.isBatchMode;

    internal static bool IsServer()
    {
        if (Dedicated())
        {
            return true;
        }

        return ZNet.instance != null && ZNet.instance.IsServer();
    }
}
