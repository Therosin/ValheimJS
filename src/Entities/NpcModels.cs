using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ValheimJS.Entities;

internal enum NpcIntentKind
{
    Idle,
    Stop,
    MoveTo,
    Wait,
    LookAt,
    Face
}

internal enum NpcIntentStatus
{
    Idle,
    Running,
    Arrived,
    Failed
}

/// <summary>Queued AI decision; internal to the NPC world (never crosses into JS).</summary>
internal sealed class NpcIntent
{
    private NpcIntent(NpcIntentKind kind, double x, double y, double z, bool run, double seconds, string? targetId)
    {
        Kind = kind;
        X = x;
        Y = y;
        Z = z;
        Run = run;
        Seconds = seconds;
        TargetId = targetId;
    }

    internal NpcIntentKind Kind { get; }

    internal double X { get; }

    internal double Y { get; }

    internal double Z { get; }

    internal bool Run { get; }

    internal double Seconds { get; }

    internal string? TargetId { get; }

    internal static NpcIntent Idle { get; } = new(NpcIntentKind.Idle, 0d, 0d, 0d, false, 0d, null);

    internal static NpcIntent Stop { get; } = new(NpcIntentKind.Stop, 0d, 0d, 0d, false, 0d, null);

    internal static NpcIntent MoveTo(double x, double y, double z, bool run) =>
        new(NpcIntentKind.MoveTo, x, y, z, run, 0d, null);

    internal static NpcIntent Wait(double seconds) => new(NpcIntentKind.Wait, 0d, 0d, 0d, false, seconds, null);

    internal static NpcIntent LookAt(double x, double y, double z) =>
        new(NpcIntentKind.LookAt, x, y, z, false, 0d, null);

    internal static NpcIntent Face(string targetId) => new(NpcIntentKind.Face, 0d, 0d, 0d, false, 0d, targetId);
}

/// <summary>Serializable view of the current intent and its last outcome.</summary>
internal sealed class NpcIntentView
{
    internal string Kind { get; init; } = "idle";

    internal string Status { get; init; } = "idle";

    internal string? Reason { get; init; }

    internal double? X { get; init; }

    internal double? Y { get; init; }

    internal double? Z { get; init; }

    internal string? TargetId { get; init; }

    internal Dictionary<string, object?> ToPlain()
    {
        var target = X.HasValue
            ? new Dictionary<string, object?> { ["x"] = X.Value, ["y"] = Y ?? 0d, ["z"] = Z ?? 0d }
            : null;

        return new Dictionary<string, object?>
        {
            ["kind"] = Kind,
            ["status"] = Status,
            ["reason"] = Reason,
            ["targetId"] = TargetId,
            ["target"] = target
        };
    }
}

/// <summary>
/// One recognized entity in an NPC's perception store. Records persist after the entity leaves
/// range so behaviours can remember last-known positions, recognition counts and interactions.
/// </summary>
internal sealed class NpcSensedEntity
{
    internal string Id { get; set; } = "";
    internal string? Prefab { get; set; }
    internal string Name { get; set; } = "";
    internal string Kind { get; set; } = "";
    internal string Faction { get; set; } = "";
    internal double X { get; set; }
    internal double Y { get; set; }
    internal double Z { get; set; }
    internal float Distance { get; set; }
    internal float Bearing { get; set; }
    internal float Health { get; set; }
    internal float MaxHealth { get; set; }
    internal bool IsPlayer { get; set; }
    internal bool IsNpc { get; set; }
    internal bool IsTamed { get; set; }
    internal bool IsDead { get; set; }
    internal int Level { get; set; }
    internal bool IsObject { get; set; }
    internal bool Enemy { get; set; }
    internal bool Visible { get; set; }
    internal bool InRange { get; set; }
    internal float FirstSeenAt { get; set; }
    internal float LastSeenAt { get; set; } = -1f;
    internal int SeenCount { get; set; }

    internal Dictionary<string, object?> ToPlain(double originX, double originY, double originZ, float yaw, float now)
    {
        var dx = X - originX;
        var dy = Y - originY;
        var dz = Z - originZ;
        var distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        var radians = yaw * Math.PI / 180d;
        var forwardX = Math.Sin(radians);
        var forwardZ = Math.Cos(radians);
        var cross = forwardX * dz - forwardZ * dx;
        var dot = forwardX * dx + forwardZ * dz;
        var bearing = Math.Abs(cross) < 0.000001d && Math.Abs(dot) < 0.000001d
            ? 0d
            : Math.Atan2(cross, dot) * 180d / Math.PI;

        return new Dictionary<string, object?>
        {
            ["id"] = Id,
            ["name"] = Name,
            ["kind"] = Kind,
            ["prefab"] = Prefab,
            ["faction"] = Faction,
            ["pos"] = new Dictionary<string, object?> { ["x"] = X, ["y"] = Y, ["z"] = Z },
            ["rel"] = new Dictionary<string, object?> { ["x"] = dx, ["y"] = dy, ["z"] = dz },
            ["distance"] = distance,
            ["bearing"] = bearing,
            ["visible"] = Visible,
            ["inRange"] = InRange,
            ["enemy"] = Enemy,
            ["isPlayer"] = IsPlayer,
            ["isNpc"] = IsNpc,
            ["isTamed"] = IsTamed,
            ["isDead"] = IsDead,
            ["level"] = Level,
            ["isObject"] = IsObject,
            ["health"] = Health,
            ["maxHealth"] = MaxHealth,
            ["firstSeen"] = (double)(now - FirstSeenAt),
            ["lastSeen"] = LastSeenAt < 0f ? -1d : (double)(now - LastSeenAt),
            ["seenCount"] = SeenCount
        };
    }
}

/// <summary>Filter for <c>npc.sensed</c>. Direction defaults to the NPC's facing.</summary>
internal sealed class NpcSenseQuery
{
    internal string? Id { get; set; }
    internal float Radius { get; set; } = -1f;
    internal double? DirectionX { get; set; }
    internal double? DirectionZ { get; set; }
    internal float Angle { get; set; } = 180f;
    internal IReadOnlyList<string> Kinds { get; set; } = Array.Empty<string>();
    internal bool VisibleOnly { get; set; }
    internal bool EnemyOnly { get; set; }
    internal bool PlayersOnly { get; set; }
    internal bool IncludeRemembered { get; set; }
    internal int Max { get; set; } = 32;

    internal bool Allows(NpcSensedEntity entry, double originX, double originZ, double forwardX, double forwardZ)
    {
        if (Id != null && !string.Equals(Id, entry.Id, StringComparison.Ordinal))
        {
            return false;
        }

        if (!IncludeRemembered && !entry.InRange)
        {
            return false;
        }

        if (VisibleOnly && !entry.Visible)
        {
            return false;
        }

        if (EnemyOnly && !entry.Enemy)
        {
            return false;
        }

        if (PlayersOnly && !entry.IsPlayer)
        {
            return false;
        }

        var dx = entry.X - originX;
        var dz = entry.Z - originZ;
        var distance = Math.Sqrt(dx * dx + dz * dz);
        if (Radius >= 0f && distance > Radius)
        {
            return false;
        }

        if (Angle < 180f && distance > 0.0001d)
        {
            var cross = forwardX * dz - forwardZ * dx;
            var dot = forwardX * dx + forwardZ * dz;
            var bearing = Math.Atan2(cross, dot) * 180d / Math.PI;
            if (Math.Abs(bearing) > Angle)
            {
                return false;
            }
        }

        return MatchesKinds(entry);
    }

    private bool MatchesKinds(NpcSensedEntity entry)
    {
        if (Kinds.Count == 0)
        {
            return true;
        }

        foreach (var kind in Kinds)
        {
            if (string.IsNullOrWhiteSpace(kind))
            {
                continue;
            }

            switch (kind.ToLowerInvariant())
            {
                case "player" when entry.IsPlayer:
                case "npc" when entry.IsNpc:
                case "creature" when !entry.IsPlayer && !entry.IsNpc:
                case "tamed" when entry.IsTamed:
                case "enemy" when entry.Enemy:
                case "dead" when entry.IsDead:
                    return true;
            }

            if (string.Equals(entry.Kind, kind, StringComparison.Ordinal) ||
                string.Equals(entry.Prefab, kind, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Perception tuning for one NPC kind.</summary>
internal sealed class NpcSensors
{
    internal float Radius { get; init; } = 20f;

    internal int Max { get; init; } = 8;

    internal float ViewRange { get; init; } = 20f;

    /// <summary>Half-angle in degrees; 90 matches the game's default front hemisphere.</summary>
    internal float ViewAngle { get; init; } = 90f;

    internal bool LineOfSight { get; init; }

    internal bool IncludePlayers { get; init; } = true;

    internal bool IncludeCreatures { get; init; } = true;

    internal bool IncludeTamed { get; init; } = true;

    internal bool IncludeNpcs { get; init; } = true;

    internal bool IncludeDead { get; init; }

    internal float ObjectRadius { get; init; }

    internal int ObjectMax { get; init; } = 6;

    internal NpcSensors Validate()
    {
        if (Radius < 1f || Radius > 64f)
        {
            throw new ArgumentException("npc: perception.radius must be between 1 and 64");
        }

        if (Max < 1 || Max > 32)
        {
            throw new ArgumentException("npc: perception.max must be between 1 and 32");
        }

        if (ViewRange < 0f || ViewRange > 128f)
        {
            throw new ArgumentException("npc: perception.viewRange must be between 0 and 128");
        }

        if (ViewAngle < 0f || ViewAngle > 180f)
        {
            throw new ArgumentException("npc: perception.viewAngle must be between 0 and 180");
        }

        if (ObjectRadius < 0f || ObjectRadius > 64f)
        {
            throw new ArgumentException("npc: perception.objectRadius must be between 0 and 64");
        }

        if (ObjectMax < 1 || ObjectMax > 32)
        {
            throw new ArgumentException("npc: perception.objectMax must be between 1 and 32");
        }

        return this;
    }
}

internal sealed class NpcDefinition
{
    internal string Kind { get; private init; } = "";

    internal string Prefab { get; private init; } = "";

    internal double TickMs { get; private init; } = 500d;

    internal string? Faction { get; private init; }

    internal NpcSensors Sensors { get; private init; } = new();

    internal static bool TryCreate(
        string? kind,
        string? prefab,
        double tickMs,
        string? faction,
        NpcSensors? sensors,
        out NpcDefinition definition,
        out string error)
    {
        definition = new NpcDefinition();
        error = string.Empty;

        if (string.IsNullOrEmpty(kind) || !Regex.IsMatch(kind, "^[a-z0-9_-]{1,32}$"))
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

        if (faction != null && faction.Length > 32)
        {
            error = "npc definition: faction must be at most 32 characters";
            return false;
        }

        NpcSensors effective;
        try
        {
            effective = (sensors ?? new NpcSensors()).Validate();
        }
        catch (ArgumentException exception)
        {
            error = exception.Message;
            return false;
        }

        definition = new NpcDefinition
        {
            Kind = kind!,
            Prefab = prefab!,
            TickMs = Math.Min(5000d, Math.Max(100d, tickMs)),
            Faction = faction,
            Sensors = effective
        };

        return true;
    }
}

internal sealed class NpcEnvironment
{
    internal int Day { get; init; }
    internal bool IsDay { get; init; }
    internal string Biome { get; init; } = "";
    internal string? Weather { get; init; }

    internal Dictionary<string, object?> ToPlain() => new()
    {
        ["day"] = Day,
        ["isDay"] = IsDay,
        ["biome"] = Biome,
        ["weather"] = Weather
    };
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
    internal bool IsAlive { get; init; }
    internal string Faction { get; init; } = "";
    internal bool IsTamed { get; init; }
    internal int Level { get; init; }
    internal double HomeX { get; init; }
    internal double HomeY { get; init; }
    internal double HomeZ { get; init; }
    internal NpcIntentView? Intent { get; init; }
    internal NpcEnvironment? Environment { get; init; }
    internal IReadOnlyList<NpcSensedEntity> Nearby { get; init; } = Array.Empty<NpcSensedEntity>();
    internal IReadOnlyList<NpcSensedEntity> Remembered { get; init; } = Array.Empty<NpcSensedEntity>();
    internal IReadOnlyList<NpcSensedEntity> Objects { get; init; } = Array.Empty<NpcSensedEntity>();

    internal Dictionary<string, object?> ToPlain(float yaw, float now)
    {
        var nearby = new List<object?>(Nearby.Count);
        foreach (var entry in Nearby)
        {
            nearby.Add(entry.ToPlain(X, Y, Z, yaw, now));
        }

        var remembered = new List<object?>(Remembered.Count);
        foreach (var entry in Remembered)
        {
            remembered.Add(entry.ToPlain(X, Y, Z, yaw, now));
        }

        var objects = new List<object?>(Objects.Count);
        foreach (var entry in Objects)
        {
            objects.Add(entry.ToPlain(X, Y, Z, yaw, now));
        }

        return new Dictionary<string, object?>
        {
            ["id"] = Id,
            ["kind"] = Kind,
            ["x"] = X,
            ["y"] = Y,
            ["z"] = Z,
            ["health"] = Health,
            ["maxHealth"] = MaxHealth,
            ["isAlive"] = IsAlive,
            ["faction"] = Faction,
            ["isTamed"] = IsTamed,
            ["level"] = Level,
            ["home"] = new Dictionary<string, object?> { ["x"] = HomeX, ["y"] = HomeY, ["z"] = HomeZ },
            ["intent"] = Intent?.ToPlain(),
            ["environment"] = Environment?.ToPlain(),
            ["nearby"] = nearby,
            ["remembered"] = remembered,
            ["objects"] = objects
        };
    }
}

/// <summary>Implemented by world behaviours that keep a queryable perception store.</summary>
internal interface INpcSenses
{
    IReadOnlyList<NpcSensedEntity> Senses(NpcSenseQuery query);

    IReadOnlyList<NpcSensedEntity> TakeNewSenses();
}
