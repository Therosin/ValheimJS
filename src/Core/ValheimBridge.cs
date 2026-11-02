using System;
using System.Collections.Generic;

namespace ValheimJS.Core;

/// <summary>
/// Bridge between game-side capture points (Harmony patches) and the JS host.
/// Patches call <see cref="Emit"/>; the plugin subscribes to <see cref="Emit"/> and
/// dispatches the matching JS hook. Kept static so patches need no plugin instance.
/// </summary>
public static class ValheimBridge
{
    /// <summary>(hookName, payloadJson) → delivered to every loaded JS plugin.</summary>
    public static Action<string, string>? Emit { get; set; }

    /// <summary>Provides the JSON world snapshot for <c>host.world()</c>.</summary>
    public static Func<string>? WorldSnapshot { get; set; }

    public static void RaiseChat(string channel, long senderId, string text)
    {
        Emit?.Invoke("onChat", $"{{\"channel\":{Quote(channel)},\"senderId\":{senderId},\"text\":{Quote(text)}}}");
    }

    public static void RaisePlayerJoin(long uid, string name)
    {
        Emit?.Invoke("onPlayerJoin", $"{{\"uid\":{uid},\"name\":{Quote(name)}}}");
    }

    public static void RaisePlayerSpawned(long uid, string name)
    {
        Emit?.Invoke("onPlayerSpawned", $"{{\"uid\":{uid},\"name\":{Quote(name)}}}");
    }

    public static void RaisePlayerDespawned(long uid, string name)
    {
        Emit?.Invoke("onPlayerDespawned", $"{{\"uid\":{uid},\"name\":{Quote(name)}}}");
    }

    public static void RaisePlayerLeave(long uid, string name)
    {
        PlayerAccess.Forget(uid);
        Emit?.Invoke("onPlayerLeave", $"{{\"uid\":{uid},\"name\":{Quote(name)}}}");
    }

    public static void RaiseDamage(
        string target,
        bool targetIsPlayer,
        string attacker,
        bool attackerIsPlayer,
        float damage,
        float targetHealth,
        float targetMaxHealth,
        string hitType)
    {
        var json = "{" +
                   $"\"target\":{Quote(target)}," +
                   $"\"targetIsPlayer\":{(targetIsPlayer ? "true" : "false")}," +
                   $"\"attacker\":{Quote(attacker)}," +
                   $"\"attackerIsPlayer\":{(attackerIsPlayer ? "true" : "false")}," +
                   $"\"damage\":{F(damage)}," +
                   $"\"targetHealth\":{F(targetHealth)}," +
                   $"\"targetMaxHealth\":{F(targetMaxHealth)}," +
                   $"\"hitType\":{Quote(hitType)}" +
                   "}";
        Emit?.Invoke("onDamage", json);
    }

    public static void RaiseServerTick()
    {
        Emit?.Invoke("onServerTick", WorldSnapshot?.Invoke() ?? "{}");
    }

    private static string F(float value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    internal static string Quote(string? value) => ValheimJS.Utils.Json.Quote(value);
}
