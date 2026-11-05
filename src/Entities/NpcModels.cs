using System;
using System.Collections.Generic;

namespace ValheimJS.Entities;

internal enum NpcIntentKind
{
    Idle,
    MoveTo
}

/// <summary>Queued AI decision; internal to the NPC world (never crosses into JS).</summary>
internal sealed class NpcIntent
{
    private NpcIntent(NpcIntentKind kind, double x, double y, double z, bool run)
    {
        Kind = kind;
        X = x;
        Y = y;
        Z = z;
        Run = run;
    }

    internal NpcIntentKind Kind { get; }

    internal double X { get; }

    internal double Y { get; }

    internal double Z { get; }

    internal bool Run { get; }

    internal static NpcIntent Idle { get; } = new(NpcIntentKind.Idle, 0d, 0d, 0d, false);

    internal static NpcIntent MoveTo(double x, double y, double z, bool run) => new(NpcIntentKind.MoveTo, x, y, z, run);
}

internal sealed class NpcDefinition
{
    internal string Kind { get; private init; } = "";

    internal string Prefab { get; private init; } = "";

    internal double TickMs { get; private init; } = 500d;

    internal static bool TryCreate(string? kind, string? prefab, double tickMs, out NpcDefinition definition, out string error)
    {
        definition = new NpcDefinition();
        error = string.Empty;

        if (string.IsNullOrEmpty(kind) || !System.Text.RegularExpressions.Regex.IsMatch(kind, "^[a-z0-9_-]{1,32}$"))
        {
            error = "npc definition: kind must match ^[a-z0-9_-]{1,32}$";
            return false;
        }

        if (string.IsNullOrEmpty(prefab) || prefab.Length > 64)
        {
            error = "npc definition: prefab must be 1..64 characters";
            return false;
        }

        if (double.IsNaN(tickMs) || double.IsInfinity(tickMs) || tickMs == 0d)
        {
            tickMs = 500d;
        }

        definition = new NpcDefinition
        {
            Kind = kind!,
            Prefab = prefab!,
            TickMs = Math.Min(5000d, Math.Max(100d, tickMs))
        };

        return true;
    }
}

internal sealed class NpcNearbyEntry
{
    internal string Id { get; init; } = "";
    internal string? Prefab { get; init; }
    internal string Name { get; init; } = "";
    internal double X { get; init; }
    internal double Y { get; init; }
    internal double Z { get; init; }
    internal float Distance { get; init; }
    internal float Health { get; init; }
    internal bool IsPlayer { get; init; }
}

internal sealed class NpcPerception
{
    internal string Id { get; init; } = "";
    internal string Kind { get; init; } = "";
    internal double X { get; init; }
    internal double Y { get; init; }
    internal double Z { get; init; }
    internal float Health { get; init; }
    internal float MaxHealth { get; init; }
    internal NpcIntentKind? LastIntentKind { get; init; }
    internal bool? LastIntentOk { get; init; }
    internal string? LastIntentReason { get; init; }
    internal IReadOnlyList<NpcNearbyEntry> Nearby { get; init; } = Array.Empty<NpcNearbyEntry>();
}
