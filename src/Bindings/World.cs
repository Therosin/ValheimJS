using System;
using ValheimJS.Scripting.Binding;
using UnityEngine;

namespace ValheimJS.Bindings;

/// <summary>
/// Read-only world/peer information for the server. Scripts see <c>host.api.world.*</c>.
/// Values are read live on each call — at boot ZNet is not initialized yet, so they read as
/// null/zero until the world loads. Never cache them as truth.
/// </summary>
[JsBinding("World", JsBindPhase.Gameplay, order: 100)]
internal static class WorldBindings
{
    public static void Bind(JsBindingContext context)
    {
        context.Add("world.snapshot", new Func<string>(Snapshot), JsReturnKind.Json);
        context.Add("world.name", new Func<string?>(() => ZNet.instance != null ? ZNet.instance.GetWorldName() : null));
        context.Add("world.peers", new Func<int>(() => ZNet.instance?.GetPeers()?.Count ?? 0));
        context.Add("world.day", new Func<int>(() => EnvMan.instance != null ? EnvMan.instance.GetDay() : 0));
        context.Add("world.dayFraction", new Func<double>(() =>
            EnvMan.instance != null ? EnvMan.instance.GetDayFraction() : 0d));
        context.Add("world.timeOfDay", new Func<string>(() => TimeOfDay()));
    }

    private static string TimeOfDay() =>
        EnvMan.instance != null ? (EnvMan.IsDay() ? "day" : "night") : "unknown";

    private static string Snapshot()
    {
        var name = ZNet.instance != null ? ZNet.instance.GetWorldName() : null;
        var peers = ZNet.instance?.GetPeers()?.Count ?? 0;
        var day = EnvMan.instance != null ? EnvMan.instance.GetDay() : 0;
        var fraction = EnvMan.instance != null ? EnvMan.instance.GetDayFraction() : 0d;

        return "{" +
               $"\"world\":{ValheimJS.Utils.Json.Quote(name)}," +
               $"\"peers\":{peers}," +
               $"\"day\":{day}," +
               $"\"dayFraction\":{fraction.ToString(System.Globalization.CultureInfo.InvariantCulture)}," +
               $"\"timeOfDay\":\"{TimeOfDay()}\"" +
               "}";
    }
}
