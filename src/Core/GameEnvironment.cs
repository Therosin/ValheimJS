using UnityEngine;
using System;
using ValheimJS.Scripting;

namespace ValheimJS.Core;

/// <summary>
/// Process-side facts. <c>Dedicated</c> is fixed at startup; <c>IsServer</c> is live because a
/// windowed process can start hosting a world later. Prefer <c>IsServer</c> for authority.
/// </summary>
public static class GameEnvironment
{
    /// <summary>Dedicated-server process branch, independent of rendering or world readiness.</summary>
    private static ProcessSide? _side;
    public static ProcessSide Side => _side ?? throw new InvalidOperationException("Process side has not been initialized.");
    internal static void Initialize(ProcessSide side)
    {
        if (_side.HasValue && _side.Value != side) throw new InvalidOperationException("Process side cannot change during this process.");
        _side = side;
    }
    public static bool Dedicated => Side == ProcessSide.Server;
    public static bool SessionReady => ZNet.instance != null;
    public static void ServerOnly(Action action) { if (Dedicated) action(); }
    public static void ClientOnly(Action action) { if (!Dedicated) action(); }
    public static void AuthorityOnly(Action action) { if (IsServer()) action(); }

    public static bool IsServer()
    {
        return ZNet.instance != null && ZNet.instance.IsServer();
    }

    public static bool SinglePlayer => ZNet.IsSinglePlayer;

    public static int Peers => ZNet.instance?.GetPeers()?.Count ?? 0;

    public static string? WorldName => ZNet.instance != null ? ZNet.instance.GetWorldName() : null;

    public static int Day => EnvMan.instance != null ? EnvMan.instance.GetDay() : 0;

    public static double DayFraction => EnvMan.instance != null ? EnvMan.instance.GetDayFraction() : 0d;

    public static string TimeOfDay => EnvMan.instance != null ? (EnvMan.IsDay() ? "day" : "night") : "unknown";
}
