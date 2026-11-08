using System;
using System.Collections.Generic;
using UnityEngine;
using ValheimJS.Entities;

namespace ValheimJS.World;

/// <summary>
/// Server-side NPC simulation: navigation, intents, perception and ZDO writes. The vanilla AI is
/// removed from the prefab, so this behaviour is the only decision layer; JS drives it through
/// intents and reads its perception snapshots.
/// </summary>
internal sealed class NpcBehavior : IWorldBehavior, INpcSenses
{
    private const float WalkSpeed = 2f;
    private const float RunSpeed = 5f;
    private const float ArriveDistance = 0.75f;
    private const float WaypointDistance = 0.4f;
    private const float RepathSeconds = 1f;
    private const float StuckSeconds = 5f;
    private const float WanderRadius = 6f;
    private const float PerceptionCacheSeconds = 0.1f;

    private static readonly int ViewBlockMask = LayerMask.GetMask(
        "Default", "static_solid", "Default_small", "piece", "terrain", "viewblock", "vehicle");

    private readonly NpcSensors _sensors;
    private readonly List<Vector3> _path = new();
    private readonly System.Random _random = new();
    private readonly Dictionary<string, NpcSensedEntity> _entities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NpcSensedEntity> _objects = new(StringComparer.Ordinal);
    private readonly List<NpcSensedEntity> _newEntities = new();
    private Vector3 _origin;
    private float _originYaw;
    private float _now;

    private NpcIntent _intent = NpcIntent.Idle;
    private NpcIntentStatus _status = NpcIntentStatus.Idle;
    private string? _reason;
    private float _intentAt;
    private NpcPerception? _perception;
    private float _perceptionAt = -1f;
    private int _perceptionGeneration = -1;

    private int _lastGeneration;
    private int _writtenGeneration = -1;
    private int _pathIndex;
    private float _repathAt;
    private float _progressAt;
    private float _bestDistance = float.MaxValue;
    private float _wanderAt;
    private Vector3? _wanderTarget;
    private bool _navMissingLogged;

    internal NpcBehavior(WorldObject obj, NpcSensors? sensors = null)
    {
        _sensors = sensors ?? new NpcSensors();
        _lastGeneration = obj.Generation;
        var zdo = obj.Zdo;
        if (zdo == null)
        {
            return;
        }

        if (zdo.GetString(WorldKeys.IntentKind, out var kind))
        {
            if (kind == "moveTo")
            {
                _intent = NpcIntent.MoveTo(
                    zdo.GetFloat(WorldKeys.IntentX, obj.Position.x),
                    zdo.GetFloat(WorldKeys.IntentY, obj.Position.y),
                    zdo.GetFloat(WorldKeys.IntentZ, obj.Position.z),
                    zdo.GetBool(WorldKeys.IntentRun));
                _status = NpcIntentStatus.Running;
            }
            else if (kind == "stop")
            {
                _intent = NpcIntent.Stop;
            }
        }
    }

    public void Tick(WorldObject obj, float dt)
    {
        if (obj.Generation != _lastGeneration)
        {
            _lastGeneration = obj.Generation;
            _path.Clear();
            _pathIndex = 0;
            _perception = null;
        }

        switch (_intent.Kind)
        {
            case NpcIntentKind.Idle:
                TickIdle(obj, dt);
                break;
            case NpcIntentKind.Stop:
                Stop(obj);
                break;
            case NpcIntentKind.MoveTo:
                TickMoveTo(obj, dt);
                break;
            case NpcIntentKind.Wait:
                if (Time.realtimeSinceStartup - _intentAt >= (float)_intent.Seconds)
                {
                    _status = NpcIntentStatus.Arrived;
                    Stop(obj);
                }

                break;
            case NpcIntentKind.LookAt:
            case NpcIntentKind.Face:
                Stop(obj);
                break;
        }

        Refresh(obj);
    }

    public void Write(WorldObject obj, ZDO zdo)
    {
        if (_writtenGeneration == obj.Generation)
        {
            return;
        }

        _writtenGeneration = obj.Generation;
        zdo.Set(WorldKeys.IntentKind, _intent.Kind switch
        {
            NpcIntentKind.MoveTo => "moveTo",
            NpcIntentKind.Stop => "stop",
            _ => "idle"
        });
        zdo.Set(WorldKeys.IntentX, (float)_intent.X);
        zdo.Set(WorldKeys.IntentY, (float)_intent.Y);
        zdo.Set(WorldKeys.IntentZ, (float)_intent.Z);
        zdo.Set(WorldKeys.IntentRun, _intent.Run);
        zdo.Set(WorldKeys.Generation, obj.Generation);
    }

    public object? Snapshot(WorldObject obj)
    {
        var now = Time.realtimeSinceStartup;
        if (_perception == null || _perceptionGeneration != obj.Generation ||
            now - _perceptionAt >= PerceptionCacheSeconds)
        {
            Refresh(obj);
        }

        return _perception;
    }

    public bool Command(WorldObject obj, string command, object?[] args)
    {
        var now = Time.realtimeSinceStartup;
        switch (command)
        {
            case "moveTo" when args.Length >= 3:
                _intent = NpcIntent.MoveTo(Convert.ToDouble(args[0]), Convert.ToDouble(args[1]),
                    Convert.ToDouble(args[2]), args.Length > 3 && Convert.ToBoolean(args[3]));
                _status = NpcIntentStatus.Running;
                _reason = null;
                _intentAt = now;
                Reset(obj);
                return true;
            case "idle":
                _intent = NpcIntent.Idle;
                _status = NpcIntentStatus.Idle;
                _reason = null;
                _intentAt = now;
                Reset(obj);
                return true;
            case "stop":
                _intent = NpcIntent.Stop;
                _status = NpcIntentStatus.Idle;
                _reason = null;
                _intentAt = now;
                Stop(obj);
                Reset(obj);
                return true;
            case "wait" when args.Length >= 1:
            {
                var seconds = Convert.ToDouble(args[0]);
                if (double.IsNaN(seconds) || double.IsInfinity(seconds))
                {
                    return false;
                }

                _intent = NpcIntent.Wait(Math.Clamp(seconds, 0d, 3600d));
                _status = NpcIntentStatus.Running;
                _reason = null;
                _intentAt = now;
                Stop(obj);
                Reset(obj);
                if (_intent.Seconds <= 0d)
                {
                    _status = NpcIntentStatus.Arrived;
                }

                return true;
            }
            case "lookAt" when args.Length >= 3:
                _intent = NpcIntent.LookAt(Convert.ToDouble(args[0]), Convert.ToDouble(args[1]), Convert.ToDouble(args[2]));
                _status = NpcIntentStatus.Arrived;
                _reason = null;
                _intentAt = now;
                Stop(obj);
                SetYaw(obj, _intent.X, _intent.Z);
                Reset(obj);
                return true;
            case "face" when args.Length >= 1:
            {
                var handle = args[0] as string ?? string.Empty;
                var target = TargetPosition(handle);
                if (!target.HasValue)
                {
                    _intent = NpcIntent.Face(handle);
                    _status = NpcIntentStatus.Failed;
                    _reason = "target-missing";
                    _intentAt = now;
                    return true;
                }

                _intent = NpcIntent.Face(handle);
                _status = NpcIntentStatus.Arrived;
                _reason = null;
                _intentAt = now;
                Stop(obj);
                SetYaw(obj, target.Value.x, target.Value.z);
                Reset(obj);
                return true;
            }
            default:
                return false;
        }
    }

    internal static float HealthOf(ZDOID id, float fallback)
    {
        var zdo = ZDOMan.instance?.GetZDO(id);
        return zdo != null ? zdo.GetFloat(ZDOVars.s_health, fallback) : 0f;
    }

    internal static float MaxHealthOf(ZDOID id)
    {
        var zdo = ZDOMan.instance?.GetZDO(id);
        if (zdo == null)
        {
            return 0f;
        }

        var prefab = ZNetScene.instance?.GetPrefab(zdo.GetPrefab());
        var character = prefab != null ? prefab.GetComponent<Character>() : null;
        return zdo.GetFloat(ZDOVars.s_maxHealth, character != null ? character.m_health : 0f);
    }

    internal static bool IsAlive(ZDOID id, float health)
    {
        var instance = ZNetScene.instance?.FindInstance(id);
        var character = instance != null ? instance.GetComponent<Character>() : null;
        return character != null ? !character.IsDead() : health > 0f;
    }

    private void Refresh(WorldObject obj)
    {
        _origin = obj.Position;
        _originYaw = obj.Yaw;
        _now = Time.realtimeSinceStartup;
        _perception = BuildPerception(obj);
        _perceptionAt = _now;
        _perceptionGeneration = obj.Generation;
    }

    private NpcPerception BuildPerception(WorldObject obj)
    {
        var scene = ZNetScene.instance;
        var zdo = obj.Zdo;
        var prefab = zdo != null && scene != null ? scene.GetPrefab(zdo.GetPrefab()) : null;
        var prefabCharacter = prefab != null ? prefab.GetComponent<Character>() : null;
        var liveCharacter = scene?.FindInstance(obj.Id)?.GetComponent<Character>();
        var character = liveCharacter != null ? liveCharacter : prefabCharacter;

        var health = HealthOf(obj.Id, character != null ? character.m_health : 0f);
        var maxHealth = MaxHealthOf(obj.Id);
        var faction = character != null ? character.GetFaction() : Character.Faction.AnimalsVeg;
        var isTamed = liveCharacter != null
            ? liveCharacter.IsTamed()
            : zdo != null && zdo.GetBool(ZDOVars.s_tamed, false);
        var level = liveCharacter != null
            ? liveCharacter.GetLevel()
            : zdo != null ? zdo.GetInt(ZDOVars.s_level, 1) : 1;

        return new NpcPerception
        {
            Id = obj.Handle,
            Kind = obj.Kind,
            X = obj.Position.x,
            Y = obj.Position.y,
            Z = obj.Position.z,
            Health = health,
            MaxHealth = maxHealth,
            IsAlive = IsAlive(obj.Id, health),
            Faction = faction.ToString(),
            IsTamed = isTamed,
            Level = level,
            HomeX = obj.Home.x,
            HomeY = obj.Home.y,
            HomeZ = obj.Home.z,
            Intent = new NpcIntentView
            {
                Kind = IntentName(_intent.Kind),
                Status = StatusName(_status),
                Reason = _reason,
                X = _intent.Kind is NpcIntentKind.MoveTo or NpcIntentKind.LookAt ? _intent.X : null,
                Y = _intent.Kind is NpcIntentKind.MoveTo or NpcIntentKind.LookAt ? _intent.Y : null,
                Z = _intent.Kind is NpcIntentKind.MoveTo or NpcIntentKind.LookAt ? _intent.Z : null,
                TargetId = _intent.Kind == NpcIntentKind.Face ? _intent.TargetId : null
            },
            Environment = new NpcEnvironment
            {
                Day = ZNet.instance != null ? EnvMan.instance?.GetDay() ?? 0 : 0,
                IsDay = ZNet.instance != null && EnvMan.IsDay(),
                Biome = WorldGenerator.instance != null
                    ? WorldGenerator.instance.GetBiome(obj.Position).ToString()
                    : string.Empty,
                Weather = EnvMan.instance?.GetCurrentEnvironment()?.m_name
            },
            Nearby = ScanCharacters(obj, character, faction, isTamed),
            Remembered = Remembered(),
            Objects = ScanObjects(obj)
        };
    }

    private IReadOnlyList<NpcSensedEntity> ScanCharacters(
        WorldObject obj,
        Character? self,
        Character.Faction selfFaction,
        bool selfTamed)
    {
        var nearby = new List<NpcSensedEntity>();
        var manager = ZDOMan.instance;
        var scene = ZNetScene.instance;
        if (manager == null || scene == null)
        {
            return nearby;
        }

        foreach (var entry in _entities.Values)
        {
            entry.InRange = false;
            entry.Visible = false;
        }

        var objects = new List<ZDO>();
        manager.FindSectorObjects(ZoneSystem.GetZone(obj.Position), new SimulationDistance(1, 0, false), objects);
        var selfGroup = self?.m_group ?? string.Empty;
        var forward = Quaternion.Euler(0f, obj.Yaw, 0f) * Vector3.forward;

        foreach (var zdo in objects)
        {
            if (zdo.m_uid == obj.Id)
            {
                continue;
            }

            var prefab = scene.GetPrefab(zdo.GetPrefab());
            var prefabCharacter = prefab != null ? prefab.GetComponent<Character>() : null;
            if (prefabCharacter == null)
            {
                continue;
            }

            var position = zdo.GetPosition();
            var distance = Vector3.Distance(obj.Position, position);
            if (distance > _sensors.Radius)
            {
                continue;
            }

            var live = scene.FindInstance(zdo.m_uid)?.GetComponent<Character>();
            var character = live != null ? live : prefabCharacter;
            var isPlayer = character.IsPlayer();
            var isNpc = zdo.GetString(WorldKeys.Kind, out _);
            var isTamed = live != null ? live.IsTamed() : zdo.GetBool(ZDOVars.s_tamed, false);
            var health = zdo.GetFloat(ZDOVars.s_health, character.m_health);
            var isDead = health <= 0f || live != null && live.IsDead();

            if (!_sensors.IncludeDead && isDead)
            {
                continue;
            }

            if (isPlayer && !_sensors.IncludePlayers)
            {
                continue;
            }

            if (isNpc && !_sensors.IncludeNpcs)
            {
                continue;
            }

            if (!isPlayer && !isNpc)
            {
                if (!_sensors.IncludeCreatures)
                {
                    continue;
                }

                if (isTamed && !_sensors.IncludeTamed)
                {
                    continue;
                }
            }

            var handle = zdo.m_uid.ToString();
            if (!_entities.TryGetValue(handle, out var entry))
            {
                entry = new NpcSensedEntity { Id = handle, FirstSeenAt = _now };
                _entities[handle] = entry;
                _newEntities.Add(entry);
            }

            var visible = IsVisible(obj, position, distance, forward);
            entry.Prefab = prefab != null ? prefab.name : null;
            entry.Name = character.m_name;
            entry.Kind = zdo.GetString(WorldKeys.Kind, out var npcKind) ? npcKind : string.Empty;
            entry.Faction = character.GetFaction().ToString();
            entry.X = position.x;
            entry.Y = position.y;
            entry.Z = position.z;
            entry.Distance = distance;
            entry.Bearing = Bearing(obj, position, forward);
            entry.Health = health;
            entry.MaxHealth = live != null ? live.GetMaxHealth() : character.m_health;
            entry.IsPlayer = isPlayer;
            entry.IsNpc = isNpc;
            entry.IsTamed = isTamed;
            entry.IsDead = isDead;
            entry.Level = live != null ? live.GetLevel() : zdo.GetInt(ZDOVars.s_level, 1);
            entry.Enemy = IsEnemy(selfFaction, selfTamed, selfGroup, character.GetFaction(), isTamed, character.m_group);
            entry.Visible = visible;
            entry.InRange = true;

            if (visible)
            {
                entry.LastSeenAt = _now;
                entry.SeenCount++;
            }

            nearby.Add(entry);
        }

        PruneSenses();
        nearby.Sort((left, right) => left.Distance.CompareTo(right.Distance));
        return nearby.Count > _sensors.Max ? nearby.GetRange(0, _sensors.Max) : nearby;
    }

    private IReadOnlyList<NpcSensedEntity> Remembered()
    {
        var remembered = new List<NpcSensedEntity>();
        foreach (var entry in _entities.Values)
        {
            if (!entry.InRange)
            {
                remembered.Add(entry);
            }
        }

        foreach (var entry in _objects.Values)
        {
            if (!entry.InRange)
            {
                remembered.Add(entry);
            }
        }

        remembered.Sort((left, right) =>
        {
            var leftSeen = left.LastSeenAt < 0f ? left.FirstSeenAt : left.LastSeenAt;
            var rightSeen = right.LastSeenAt < 0f ? right.FirstSeenAt : right.LastSeenAt;
            return rightSeen.CompareTo(leftSeen);
        });

        return remembered.Count > 32 ? remembered.GetRange(0, 32) : remembered;
    }

    private void PruneSenses()
    {
        var stale = new List<string>();
        foreach (var pair in _entities)
        {
            var seen = pair.Value.LastSeenAt < 0f ? pair.Value.FirstSeenAt : pair.Value.LastSeenAt;
            if (pair.Value.InRange || _now - seen <= 300f)
            {
                continue;
            }

            stale.Add("c:" + pair.Key);
        }

        foreach (var pair in _objects)
        {
            var seen = pair.Value.LastSeenAt < 0f ? pair.Value.FirstSeenAt : pair.Value.LastSeenAt;
            if (pair.Value.InRange || _now - seen <= 300f)
            {
                continue;
            }

            stale.Add("o:" + pair.Key);
        }

        foreach (var key in stale)
        {
            if (key.StartsWith("c:", StringComparison.Ordinal))
            {
                _entities.Remove(key.Substring(2));
            }
            else
            {
                _objects.Remove(key.Substring(2));
            }
        }
    }

    public IReadOnlyList<NpcSensedEntity> Senses(NpcSenseQuery query)
    {
        var forwardX = Math.Sin(_originYaw * Math.PI / 180d);
        var forwardZ = Math.Cos(_originYaw * Math.PI / 180d);
        if (query.DirectionX.HasValue && query.DirectionZ.HasValue)
        {
            var length = Math.Sqrt(query.DirectionX.Value * query.DirectionX.Value +
                                   query.DirectionZ.Value * query.DirectionZ.Value);
            if (length > 0.0001d)
            {
                forwardX = query.DirectionX.Value / length;
                forwardZ = query.DirectionZ.Value / length;
            }
        }

        var matches = new List<NpcSensedEntity>();
        foreach (var entry in _entities.Values)
        {
            if (query.Allows(entry, _origin.x, _origin.z, forwardX, forwardZ))
            {
                matches.Add(entry);
            }
        }

        foreach (var entry in _objects.Values)
        {
            if (query.Allows(entry, _origin.x, _origin.z, forwardX, forwardZ))
            {
                matches.Add(entry);
            }
        }

        matches.Sort((left, right) =>
        {
            var leftDistance = (left.X - _origin.x) * (left.X - _origin.x) + (left.Z - _origin.z) * (left.Z - _origin.z);
            var rightDistance = (right.X - _origin.x) * (right.X - _origin.x) + (right.Z - _origin.z) * (right.Z - _origin.z);
            return leftDistance.CompareTo(rightDistance);
        });

        return matches.Count > query.Max ? matches.GetRange(0, query.Max) : matches;
    }

    public IReadOnlyList<NpcSensedEntity> TakeNewSenses()
    {
        if (_newEntities.Count == 0)
        {
            return Array.Empty<NpcSensedEntity>();
        }

        var result = _newEntities.ToArray();
        _newEntities.Clear();
        return result;
    }

    private IReadOnlyList<NpcSensedEntity> ScanObjects(WorldObject obj)
    {
        var entries = new List<NpcSensedEntity>();
        if (_sensors.ObjectRadius <= 0f)
        {
            return entries;
        }

        foreach (var entry in _objects.Values)
        {
            entry.InRange = false;
            entry.Visible = false;
        }

        var manager = ZDOMan.instance;
        var scene = ZNetScene.instance;
        if (manager == null || scene == null)
        {
            return entries;
        }

        var objects = new List<ZDO>();
        manager.FindSectorObjects(ZoneSystem.GetZone(obj.Position), new SimulationDistance(1, 0, false), objects);
        foreach (var zdo in objects)
        {
            if (zdo.m_uid == obj.Id)
            {
                continue;
            }

            var prefab = scene.GetPrefab(zdo.GetPrefab());
            if (prefab == null || prefab.GetComponent<Character>() != null)
            {
                continue;
            }

            var kind = Classify(prefab);
            if (kind == null)
            {
                continue;
            }

            var position = zdo.GetPosition();
            var distance = Vector3.Distance(obj.Position, position);
            if (distance > _sensors.ObjectRadius)
            {
                continue;
            }

            var handle = zdo.m_uid.ToString();
            if (!_objects.TryGetValue(handle, out var entry))
            {
                entry = new NpcSensedEntity
                {
                    Id = handle,
                    IsObject = true,
                    FirstSeenAt = _now
                };
                _objects[handle] = entry;
                _newEntities.Add(entry);
            }

            entry.Prefab = prefab.name;
            entry.Name = prefab.name;
            entry.Kind = kind;
            entry.X = position.x;
            entry.Y = position.y;
            entry.Z = position.z;
            entry.Distance = distance;
            entry.Visible = true;
            entry.InRange = true;
            entry.LastSeenAt = _now;
            entry.SeenCount++;
            entries.Add(entry);
        }

        entries.Sort((left, right) => left.Distance.CompareTo(right.Distance));
        return entries.Count > _sensors.ObjectMax ? entries.GetRange(0, _sensors.ObjectMax) : entries;
    }

    private bool IsVisible(WorldObject obj, Vector3 target, float distance, Vector3 forward)
    {
        if (distance > _sensors.ViewRange)
        {
            return false;
        }

        var to = target - obj.Position;
        to.y = 0f;
        if (to.sqrMagnitude > 0.0001f &&
            Vector3.Angle(forward, to) > _sensors.ViewAngle)
        {
            return false;
        }

        return !_sensors.LineOfSight || HasLineOfSight(obj.Position, target);
    }

    private static float Bearing(WorldObject obj, Vector3 target, Vector3 forward)
    {
        var to = target - obj.Position;
        to.y = 0f;
        return to.sqrMagnitude <= 0.0001f ? 0f : Vector3.SignedAngle(forward, to, Vector3.up);
    }

    private static bool HasLineOfSight(Vector3 from, Vector3 to)
    {
        var eye = from + Vector3.up * 1.5f;
        var target = to + Vector3.up;
        var delta = target - eye;
        var distance = delta.magnitude;
        return distance <= 0.01f || !Physics.Raycast(eye, delta / distance, distance, ViewBlockMask);
    }

    private static string? Classify(GameObject prefab)
    {
        if (prefab.GetComponent<Container>() != null)
        {
            return "container";
        }

        if (prefab.GetComponent<Door>() != null)
        {
            return "door";
        }

        if (prefab.GetComponent<Bed>() != null)
        {
            return "bed";
        }

        if (prefab.GetComponent<Smelter>() != null)
        {
            return "smelter";
        }

        if (prefab.GetComponent<CraftingStation>() != null)
        {
            return "station";
        }

        if (prefab.GetComponent<ItemDrop>() != null)
        {
            return "item";
        }

        if (prefab.GetComponent<TeleportWorld>() != null)
        {
            return "portal";
        }

        return null;
    }

    private static bool IsEnemy(
        Character.Faction self,
        bool selfTamed,
        string selfGroup,
        Character.Faction other,
        bool otherTamed,
        string otherGroup)
    {
        if (selfGroup.Length > 0 && string.Equals(selfGroup, otherGroup, StringComparison.Ordinal))
        {
            return false;
        }

        if (selfTamed || otherTamed)
        {
            if ((selfTamed && otherTamed) ||
                (selfTamed && other == Character.Faction.Players) ||
                (otherTamed && self == Character.Faction.Players) ||
                (selfTamed && other == Character.Faction.Dverger) ||
                (otherTamed && self == Character.Faction.Dverger))
            {
                return false;
            }

            return true;
        }

        if (self == other)
        {
            return false;
        }

        switch (self)
        {
            case Character.Faction.AnimalsVeg:
            case Character.Faction.PlayerSpawned:
                return true;
            case Character.Faction.Players:
                return other != Character.Faction.Dverger;
            case Character.Faction.ForestMonsters:
                return other != Character.Faction.AnimalsVeg && other != Character.Faction.Boss;
            case Character.Faction.Undead:
                return other != Character.Faction.Demon && other != Character.Faction.Boss;
            case Character.Faction.Demon:
                return other != Character.Faction.Undead && other != Character.Faction.Boss;
            case Character.Faction.MountainMonsters:
            case Character.Faction.SeaMonsters:
            case Character.Faction.PlainsMonsters:
                return other != Character.Faction.Boss;
            case Character.Faction.MistlandsMonsters:
            case Character.Faction.DeepNorth:
                return other != Character.Faction.AnimalsVeg && other != Character.Faction.Boss;
            case Character.Faction.Dverger:
                return other != Character.Faction.AnimalsVeg && other != Character.Faction.Boss &&
                       other != Character.Faction.Players;
            case Character.Faction.Boss:
                return other == Character.Faction.Players || other == Character.Faction.PlayerSpawned;
            case Character.Faction.TrainingDummy:
                return other == Character.Faction.Players;
            default:
                return false;
        }
    }

    private static string IntentName(NpcIntentKind kind) => kind switch
    {
        NpcIntentKind.MoveTo => "moveTo",
        NpcIntentKind.Wait => "wait",
        NpcIntentKind.LookAt => "lookAt",
        NpcIntentKind.Face => "face",
        NpcIntentKind.Stop => "stop",
        _ => "idle"
    };

    private static string StatusName(NpcIntentStatus status) => status switch
    {
        NpcIntentStatus.Running => "running",
        NpcIntentStatus.Arrived => "arrived",
        NpcIntentStatus.Failed => "failed",
        _ => "idle"
    };

    private static Vector3? TargetPosition(string handle)
    {
        if (string.IsNullOrWhiteSpace(handle) || !WorldManager.TryParseHandle(handle, out var id))
        {
            return null;
        }

        var zdo = ZDOMan.instance?.GetZDO(id);
        return zdo?.GetPosition();
    }

    private static void SetYaw(WorldObject obj, double x, double z)
    {
        var dx = (float)(x - obj.Position.x);
        var dz = (float)(z - obj.Position.z);
        if (dx * dx + dz * dz <= 0.0001f)
        {
            return;
        }

        obj.Yaw = Mathf.Atan2(dx, dz) * Mathf.Rad2Deg;
    }

    private void TickMoveTo(WorldObject obj, float dt)
    {
        var target = new Vector3((float)_intent.X, (float)_intent.Y, (float)_intent.Z);
        var distance = Horizontal(obj.Position, target);
        if (distance <= ArriveDistance)
        {
            Arrive(obj);
            return;
        }

        var now = Time.realtimeSinceStartup;
        if (now >= _repathAt)
        {
            _repathAt = now + RepathSeconds;
            if (!EnsurePath(obj, target))
            {
                Stop(obj);
                return;
            }
        }

        if (!Follow(obj, dt))
        {
            return;
        }

        var remaining = Horizontal(obj.Position, target);
        if (remaining <= ArriveDistance)
        {
            Arrive(obj);
            return;
        }

        if (remaining <= _bestDistance - 0.5f)
        {
            _bestDistance = remaining;
            _progressAt = now;
        }
        else if (now - _progressAt >= StuckSeconds)
        {
            Fail(obj, "nav-stuck");
        }
    }

    private void TickIdle(WorldObject obj, float dt)
    {
        var now = Time.realtimeSinceStartup;
        if (!_wanderTarget.HasValue)
        {
            Stop(obj);
            if (now < _wanderAt)
            {
                return;
            }

            var nav = WorldManager.Instance?.Navigation;
            if (nav == null)
            {
                _wanderAt = now + 3f;
                LogNavMissing();
                return;
            }

            if (!nav.TryRandomPoint(obj.Home, WanderRadius, out var target))
            {
                _wanderAt = now + 3f;
                return;
            }

            _wanderTarget = target;
            _path.Clear();
            _pathIndex = 0;
            _repathAt = 0f;
        }

        if (now >= _repathAt)
        {
            _repathAt = now + RepathSeconds;
            if (!EnsurePath(obj, _wanderTarget!.Value))
            {
                Stop(obj);
                return;
            }
        }

        if (!Follow(obj, dt))
        {
            return;
        }

        if (Horizontal(obj.Position, _wanderTarget!.Value) <= ArriveDistance)
        {
            _wanderTarget = null;
            _wanderAt = now + 3f + (float)_random.NextDouble() * 5f;
            Stop(obj);
        }
    }

    private bool Follow(WorldObject obj, float dt)
    {
        while (_pathIndex < _path.Count &&
               Horizontal(obj.Position, _path[_pathIndex]) <= WaypointDistance)
        {
            _pathIndex++;
        }

        if (_pathIndex >= _path.Count)
        {
            return true;
        }

        var speed = _intent.Kind == NpcIntentKind.MoveTo && _intent.Run ? RunSpeed : WalkSpeed;
        MoveTowards(obj, _path[_pathIndex], speed, dt);
        return true;
    }

    private bool EnsurePath(WorldObject obj, Vector3 target)
    {
        var nav = WorldManager.Instance?.Navigation;
        if (nav == null)
        {
            LogNavMissing();
            return false;
        }

        _path.Clear();
        _pathIndex = 0;
        if (nav.FindPath(obj.Position, target, _path, out var pending))
        {
            _navMissingLogged = false;
            return _path.Count > 0;
        }

        if (pending)
        {
            return false;
        }

        if (Horizontal(obj.Position, target) <= ArriveDistance)
        {
            _path.Add(target);
            return true;
        }

        Fail(obj, "nav-unavailable");
        return false;
    }

    private void MoveTowards(WorldObject obj, Vector3 target, float speed, float dt)
    {
        var delta = target - obj.Position;
        delta.y = 0f;
        var distance = delta.magnitude;
        if (distance <= 0.01f)
        {
            obj.Position.y = Mathf.MoveTowards(obj.Position.y, target.y, Mathf.Max(1f, speed) * dt);
            return;
        }

        var step = Mathf.Min(speed * dt, distance);
        var direction = delta / distance;
        obj.Position += direction * step;
        obj.Position.y = Mathf.MoveTowards(obj.Position.y, target.y, Mathf.Max(1f, speed) * dt);
        obj.Yaw = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
        obj.Speed = speed;
        obj.Moving = true;
    }

    private static float Horizontal(Vector3 left, Vector3 right)
    {
        var dx = left.x - right.x;
        var dz = left.z - right.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    private void Reset(WorldObject obj)
    {
        obj.Generation++;
        _lastGeneration = obj.Generation;
        _path.Clear();
        _pathIndex = 0;
        _repathAt = 0f;
        _bestDistance = float.MaxValue;
        _progressAt = Time.realtimeSinceStartup;
        _wanderTarget = null;
    }

    private void Arrive(WorldObject obj)
    {
        Stop(obj);
        _status = NpcIntentStatus.Arrived;
        _reason = null;
    }

    private void Fail(WorldObject obj, string reason)
    {
        Stop(obj);
        _status = NpcIntentStatus.Failed;
        _reason = reason;
    }

    private static void Stop(WorldObject obj)
    {
        obj.Speed = 0f;
        obj.Moving = false;
    }

    private void LogNavMissing()
    {
        if (_navMissingLogged)
        {
            return;
        }

        _navMissingLogged = true;
        Plugin.Logger.LogWarning("npc nav unavailable: terrain generator or zone system is not ready");
    }
}
