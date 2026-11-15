using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ValheimJS.Entities;

internal enum NpcActionKind
{
    Idle,
    Stop,
    MoveTo,
    Wait,
    LookAt,
    Face,
    Pick
}

internal enum NpcActionStatus
{
    Idle,
    Running,
    Paused,
    Arrived,
    Failed,
    Cancelled
}

/// <summary>One native body action; internal to the NPC world (never crosses into JS).</summary>
internal sealed class NpcAction
{
    private NpcAction(NpcActionKind kind, double x, double y, double z, bool run, double seconds, string? targetId)
    {
        Kind = kind;
        X = x;
        Y = y;
        Z = z;
        Run = run;
        Seconds = seconds;
        TargetId = targetId;
    }

    internal NpcActionKind Kind { get; }

    internal double X { get; }

    internal double Y { get; }

    internal double Z { get; }

    internal bool Run { get; }

    internal double Seconds { get; }

    internal string? TargetId { get; }

    internal static NpcAction Idle { get; } = new(NpcActionKind.Idle, 0d, 0d, 0d, false, 0d, null);

    internal static NpcAction Stop { get; } = new(NpcActionKind.Stop, 0d, 0d, 0d, false, 0d, null);

    internal static NpcAction MoveTo(double x, double y, double z, bool run) =>
        new(NpcActionKind.MoveTo, x, y, z, run, 0d, null);

    internal static NpcAction Wait(double seconds) => new(NpcActionKind.Wait, 0d, 0d, 0d, false, seconds, null);

    internal static NpcAction LookAt(double x, double y, double z) =>
        new(NpcActionKind.LookAt, x, y, z, false, 0d, null);

    internal static NpcAction Face(string targetId) => new(NpcActionKind.Face, 0d, 0d, 0d, false, 0d, targetId);

    internal static NpcAction Pick(string targetId) => new(NpcActionKind.Pick, 0d, 0d, 0d, false, 0d, targetId);
}

/// <summary>Serializable view of the current native action and its last outcome.</summary>
internal sealed class NpcActionView
{
    /// <summary>Monotonic per-NPC action id; increments on every issued intent.</summary>
    internal long Seq { get; init; }

    internal string Kind { get; init; } = "idle";

    internal string Status { get; init; } = "idle";

    internal string? Reason { get; init; }

    internal double? X { get; init; }

    internal double? Y { get; init; }

    internal double? Z { get; init; }

    internal string? TargetId { get; init; }

    internal double? Progress { get; init; }

    internal Dictionary<string, object?> ToPlain()
    {
        var target = X.HasValue
            ? new Dictionary<string, object?> { ["x"] = X.Value, ["y"] = Y ?? 0d, ["z"] = Z ?? 0d }
            : null;

        return new Dictionary<string, object?>
        {
            ["seq"] = Seq,
            ["kind"] = Kind,
            ["status"] = Status,
            ["reason"] = Reason,
            ["targetId"] = TargetId,
            ["target"] = target,
            ["progress"] = Progress
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
    internal bool Ready { get; set; }
    internal bool Food { get; set; }
    /// <summary>Current native noise radius emitted by a character; 0 for objects.</summary>
    internal float Noise { get; set; }
    /// <summary>Native hearing rule: within the NPC's hear range and within the target's noise radius.</summary>
    internal bool Audible { get; set; }
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
            ["ready"] = Ready,
            ["food"] = Food,
            ["noise"] = Noise,
            ["audible"] = Audible,
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
    internal bool AudibleOnly { get; set; }
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

        if (AudibleOnly && !entry.Audible)
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

    /// <summary>Hearing radius; 0 falls back to <see cref="Radius"/>.</summary>
    internal float Hearing { get; init; }

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

        if (Hearing < 0f || Hearing > 64f)
        {
            throw new ArgumentException("npc: perception.hearing must be between 0 and 64");
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

    /// <summary>Source prefab for the body; "Player" for the default human body.</summary>
    internal string Prefab { get; private init; } = "";

    internal bool Human { get; private init; }

    internal double TickMs { get; private init; } = 500d;

    internal string? Faction { get; private init; }

    /// <summary>Prefab names placed in the NPC inventory once, on first spawn.</summary>
    internal IReadOnlyList<string> Items { get; private init; } = Array.Empty<string>();

    internal NpcSensors Sensors { get; private init; } = new();

    /// <summary>Whether the body fights back when damaged. Opt-in: the default is to keep the script's intent.</summary>
    internal bool AutoEngage { get; private init; }

    internal static bool TryCreate(
        string? kind,
        string? body,
        double tickMs,
        string? faction,
        IReadOnlyList<string>? items,
        NpcSensors? sensors,
        bool autoEngage,
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

        var source = string.IsNullOrWhiteSpace(body) || string.Equals(body, "human", StringComparison.OrdinalIgnoreCase)
            ? "Player"
            : body;
        if (source.Length > 64)
        {
            error = "npc definition: body prefab must be 1..64 characters";
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

        if (items != null)
        {
            if (items.Count > 16)
            {
                error = "npc definition: items must contain at most 16 prefab names";
                return false;
            }

            foreach (var item in items)
            {
                if (string.IsNullOrWhiteSpace(item) || item.Length > 64)
                {
                    error = "npc definition: each item must be a 1..64 character prefab name";
                    return false;
                }
            }
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
            Prefab = source,
            Human = string.Equals(source, "Player", StringComparison.OrdinalIgnoreCase),
            TickMs = Math.Min(5000d, Math.Max(100d, tickMs)),
            Faction = faction,
            Items = items ?? Array.Empty<string>(),
            Sensors = effective,
            AutoEngage = autoEngage
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
    internal bool Ready { get; init; }
    internal bool Engaged { get; init; }
    internal string? ThreatId { get; init; }
    internal Dictionary<string, object?>? Self { get; init; }
    internal NpcActionView? Action { get; init; }
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
            ["ready"] = Ready,
            ["combat"] = new Dictionary<string, object?>
            {
                ["engaged"] = Engaged,
                ["threatId"] = ThreatId
            },
            ["self"] = Self,
            ["action"] = Action?.ToPlain(),
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

/// <summary>Implemented by world behaviours that settle scripted actions.</summary>
internal interface INpcActions
{
    /// <summary>Drains terminal action outcomes exactly once, in order.</summary>
    IReadOnlyList<NpcActionView> TakeActionSettlements();
}

/// <summary>One native damage event observed by an NPC body.</summary>
internal sealed class NpcDamageEvent
{
    internal string AttackerId { get; set; } = "";
    internal string AttackerName { get; set; } = "";
    internal string AttackerFaction { get; set; } = "";
    internal double X { get; set; }
    internal double Y { get; set; }
    internal double Z { get; set; }
    internal double DirectionX { get; set; }
    internal double DirectionZ { get; set; }
    internal float Damage { get; set; }
    internal float Health { get; set; }
    internal float MaxHealth { get; set; }
    internal bool IsPlayer { get; set; }
    internal bool IsNpc { get; set; }
    internal bool IsTamed { get; set; }

    internal Dictionary<string, object?> ToPlain() => new()
    {
        ["attackerId"] = AttackerId,
        ["attackerName"] = AttackerName,
        ["faction"] = AttackerFaction,
        ["pos"] = new Dictionary<string, object?> { ["x"] = X, ["y"] = Y, ["z"] = Z },
        ["direction"] = new Dictionary<string, object?> { ["x"] = DirectionX, ["z"] = DirectionZ },
        ["damage"] = Damage,
        ["health"] = Health,
        ["maxHealth"] = MaxHealth,
        ["isPlayer"] = IsPlayer,
        ["isNpc"] = IsNpc,
        ["isTamed"] = IsTamed
    };
}

/// <summary>Native hearing rule shared by scanning and tests.</summary>
internal static class NpcAudibility
{
    internal static bool IsAudible(float distance, float noise, float hearRange, bool interior)
    {
        if (hearRange <= 0f || noise <= 0f)
        {
            return false;
        }

        if (interior)
        {
            hearRange = Math.Min(12f, hearRange);
        }

        return distance <= hearRange && distance < noise;
    }
}

/// <summary>Implemented by world behaviours that surface perception and body edges.</summary>
internal interface INpcEvents
{
    IReadOnlyList<NpcSensedEntity> TakeHeard();

    IReadOnlyList<NpcDamageEvent> TakeDamage();
}
