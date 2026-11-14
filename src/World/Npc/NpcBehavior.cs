using System;
using System.Collections.Generic;
using UnityEngine;
using ValheimJS.Entities;

namespace ValheimJS.World;

/// <summary>
/// Server-side NPC simulation: native actions, perception and ZDO writes. The vanilla AI is
/// removed from the prefab, so this behaviour is the only decision layer; JS drives it through
/// actions and reads its perception snapshots.
/// </summary>
internal sealed class NpcBehavior : IWorldBehavior, INpcSenses
{
    private const float ArriveDistance = 0.75f;
    private const float PerceptionCacheSeconds = 0.1f;

    private static readonly int ViewBlockMask = LayerMask.GetMask(
        "Default", "static_solid", "Default_small", "piece", "terrain", "viewblock", "vehicle");

    private readonly NpcSensors _sensors;
    private readonly Dictionary<string, NpcSensedEntity> _entities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NpcSensedEntity> _objects = new(StringComparer.Ordinal);
    private readonly List<NpcSensedEntity> _newEntities = new();
    private Vector3 _origin;
    private float _originYaw;
    private float _now;

    private NpcAction _action = NpcAction.Idle;
    private NpcActionStatus _status = NpcActionStatus.Idle;
    private string? _reason;
    private float _actionAt;
    private NpcPerception? _perception;
    private float _perceptionAt = -1f;
    private int _perceptionGeneration = -1;

    private int _lastGeneration;
    private string _boardAction = "";
    private string _boardSelf = "";
    private bool _boardReady;
    private NpcActionKind _writtenKind = (NpcActionKind)(-1);
    private double _writtenX;
    private double _writtenY;
    private double _writtenZ;
    private double _writtenSeconds;
    private bool _writtenRun;
    private string? _writtenTarget;

    internal NpcBehavior(WorldObject obj, NpcSensors? sensors = null)
    {
        _sensors = sensors ?? new NpcSensors();
        _lastGeneration = obj.Generation;
        var zdo = obj.Zdo;
        if (zdo == null)
        {
            return;
        }

        if (!zdo.GetString(WorldKeys.ActionKind, out var kind))
        {
            return;
        }

        switch (kind)
        {
            case "moveTo":
                _action = NpcAction.MoveTo(
                    zdo.GetFloat(WorldKeys.ActionX, obj.Position.x),
                    zdo.GetFloat(WorldKeys.ActionY, obj.Position.y),
                    zdo.GetFloat(WorldKeys.ActionZ, obj.Position.z),
                    zdo.GetBool(WorldKeys.ActionRun));
                _status = NpcActionStatus.Running;
                break;
            case "lookAt":
                _action = NpcAction.LookAt(
                    zdo.GetFloat(WorldKeys.ActionX, obj.Position.x),
                    zdo.GetFloat(WorldKeys.ActionY, obj.Position.y),
                    zdo.GetFloat(WorldKeys.ActionZ, obj.Position.z));
                _status = NpcActionStatus.Running;
                break;
            case "wait":
                _action = NpcAction.Wait(zdo.GetFloat(WorldKeys.ActionSeconds, 0f));
                _status = NpcActionStatus.Running;
                break;
            case "face":
                _action = NpcAction.Face(zdo.GetString(WorldKeys.ActionTarget, ""));
                _status = NpcActionStatus.Running;
                break;
            case "pick":
                _action = NpcAction.Pick(zdo.GetString(WorldKeys.ActionTarget, ""));
                _status = NpcActionStatus.Running;
                break;
            case "stop":
                _action = NpcAction.Stop;
                break;
        }
    }

    /// <summary>Replays the current intent onto a body that was just attached.</summary>
    public void Bind(WorldObject obj)
    {
        var ai = obj.AI;
        if (ai == null)
        {
            return;
        }

        switch (_action.Kind)
        {
            case NpcActionKind.MoveTo:
                ai.GoTo(new Vector3((float)_action.X, (float)_action.Y, (float)_action.Z),
                    ArriveDistance, _action.Run);
                break;
            case NpcActionKind.LookAt:
                ai.LookAtPoint(new Vector3((float)_action.X, (float)_action.Y, (float)_action.Z));
                break;
            case NpcActionKind.Face when !string.IsNullOrWhiteSpace(_action.TargetId):
                ai.Face(_action.TargetId!);
                break;
            case NpcActionKind.Pick:
                _status = NpcActionStatus.Running;
                _reason = null;
                break;
            default:
                ai.StopCommand();
                break;
        }

        if (_action.Kind is NpcActionKind.MoveTo or NpcActionKind.LookAt or NpcActionKind.Face)
        {
            _status = ai.Status;
            _reason = ai.Reason;
        }
    }

    public void Tick(WorldObject obj, float dt)
    {
        if (obj.Generation != _lastGeneration)
        {
            _lastGeneration = obj.Generation;
            _perception = null;
        }

        if (obj.Character != null)
        {
            obj.Position = obj.Character.transform.position;
            obj.Yaw = obj.Character.transform.eulerAngles.y;
        }

        if (obj.Character != null && obj.Character.IsDead() && _status is NpcActionStatus.Running or NpcActionStatus.Paused)
        {
            _status = NpcActionStatus.Failed;
            _reason = "dead";
            obj.AI?.StopCommand();
        }

        if (obj.AI != null)
        {
            obj.Moving = obj.AI.IsMoving;
            if (_action.Kind is NpcActionKind.MoveTo or NpcActionKind.LookAt or NpcActionKind.Face)
            {
                _status = obj.AI.Status;
                _reason = obj.AI.Reason;
            }
        }
        else
        {
            obj.Moving = false;
            if (_status == NpcActionStatus.Running &&
                _action.Kind is NpcActionKind.MoveTo or NpcActionKind.LookAt or NpcActionKind.Face or NpcActionKind.Pick)
            {
                _status = NpcActionStatus.Paused;
                _reason = "body-lost";
            }
        }

        if (_action.Kind == NpcActionKind.Pick && _status is NpcActionStatus.Running or NpcActionStatus.Paused &&
            obj.AI != null)
        {
            _status = NpcActionStatus.Running;
            if (VjsActions.Pick(obj, _action.TargetId ?? "", out var reason))
            {
                _status = NpcActionStatus.Arrived;
                _reason = null;
            }
            else
            {
                _status = NpcActionStatus.Failed;
                _reason = reason;
            }
        }

        if (_action.Kind == NpcActionKind.Wait &&
            Time.realtimeSinceStartup - _actionAt >= (float)_action.Seconds)
        {
            _status = NpcActionStatus.Arrived;
            obj.AI?.StopCommand();
        }

        PublishBoard(obj);
        Refresh(obj);
    }

    private NpcActionView BuildActionView() => new()
    {
        Kind = ActionName(_action.Kind),
        Status = StatusName(_status),
        Reason = _reason,
        X = _action.Kind is NpcActionKind.MoveTo or NpcActionKind.LookAt ? _action.X : null,
        Y = _action.Kind is NpcActionKind.MoveTo or NpcActionKind.LookAt ? _action.Y : null,
        Z = _action.Kind is NpcActionKind.MoveTo or NpcActionKind.LookAt ? _action.Z : null,
        TargetId = _action.Kind is NpcActionKind.Face or NpcActionKind.Pick ? _action.TargetId : null,
        Progress = _action.Kind == NpcActionKind.Pick && _status == NpcActionStatus.Arrived ? 1d : null
    };

    private void PublishBoard(WorldObject obj)
    {
        var ready = obj.BodyReady;
        if (ready != _boardReady)
        {
            _boardReady = ready;
            NpcBoard.Publish(obj.Handle, "ready", ready);
        }

        var action = BuildActionView();
        var actionKey = $"{action.Kind}:{action.Status}:{action.Reason}:{action.Progress}";
        if (actionKey != _boardAction)
        {
            _boardAction = actionKey;
            NpcBoard.Publish(obj.Handle, "action", action.ToPlain());
        }

        if (!ready) return;
        var self = obj.Stats?.GetView();
        if (self == null) return;
        var health = self["health"] is float h ? Math.Round(h, 1) : 0d;
        var stamina = self["stamina"] is float s ? Math.Round(s, 1) : 0d;
        var food = self["food"] is System.Collections.ICollection collection ? collection.Count : 0;
        var hungry = self["hungry"] is true;
        var selfKey = $"{health}:{stamina}:{food}:{hungry}";
        if (selfKey == _boardSelf) return;
        _boardSelf = selfKey;
        NpcBoard.Publish(obj.Handle, "self", self);
        NpcBoard.Publish(obj.Handle, "hungry", hungry);
    }

    public void Write(WorldObject obj, ZDO zdo)
    {
        if (!ActionChanged())
        {
            return;
        }

        RememberAction();
        zdo.Set(WorldKeys.ActionKind, ActionName(_action.Kind));
        zdo.Set(WorldKeys.ActionX, (float)_action.X);
        zdo.Set(WorldKeys.ActionY, (float)_action.Y);
        zdo.Set(WorldKeys.ActionZ, (float)_action.Z);
        zdo.Set(WorldKeys.ActionRun, _action.Run);
        zdo.Set(WorldKeys.ActionTarget, _action.TargetId ?? string.Empty);
        zdo.Set(WorldKeys.ActionSeconds, (float)_action.Seconds);
        zdo.Set(WorldKeys.Generation, obj.Generation);
    }

    /// <summary>True when the current intent differs from what was last written to the ZDO.</summary>
    private bool ActionChanged()
    {
        if (_writtenKind != _action.Kind)
        {
            return true;
        }

        return _action.Kind switch
        {
            NpcActionKind.MoveTo => _writtenX != _action.X || _writtenY != _action.Y ||
                                    _writtenZ != _action.Z || _writtenRun != _action.Run,
            NpcActionKind.LookAt => _writtenX != _action.X || _writtenY != _action.Y ||
                                    _writtenZ != _action.Z,
            NpcActionKind.Wait => _writtenSeconds != _action.Seconds,
            NpcActionKind.Face => !string.Equals(_writtenTarget, _action.TargetId, StringComparison.Ordinal),
            _ => false
        };
    }

    private void RememberAction()
    {
        _writtenKind = _action.Kind;
        _writtenX = _action.X;
        _writtenY = _action.Y;
        _writtenZ = _action.Z;
        _writtenSeconds = _action.Seconds;
        _writtenRun = _action.Run;
        _writtenTarget = _action.TargetId;
    }

    public object? Snapshot(WorldObject obj)
    {
        if (obj.Character != null)
        {
            obj.Position = obj.Character.transform.position;
            obj.Yaw = obj.Character.transform.eulerAngles.y;
        }

        if (obj.AI != null && _action.Kind is NpcActionKind.MoveTo or NpcActionKind.LookAt or NpcActionKind.Face)
        {
            _status = obj.AI.Status;
            _reason = obj.AI.Reason;
        }

        var now = Time.realtimeSinceStartup;
        if (_perception == null || _perceptionGeneration != obj.Generation ||
            now - _perceptionAt >= PerceptionCacheSeconds)
        {
            Refresh(obj);
        }

        return _perception;
    }

    public CommandResult Command(WorldObject obj, string command, object?[] args)
    {
        var ai = obj.AI;
        var now = Time.realtimeSinceStartup;
        switch (command)
        {
            case "moveTo" when args.Length >= 3:
            {
                var x = Convert.ToDouble(args[0]);
                var y = Convert.ToDouble(args[1]);
                var z = Convert.ToDouble(args[2]);
                if (!IsFinite(x) || !IsFinite(y) || !IsFinite(z))
                {
                    return CommandResult.Fail("invalid-target");
                }

                _action = NpcAction.MoveTo(x, y, z, args.Length > 3 && Convert.ToBoolean(args[3]));
                _status = NpcActionStatus.Running;
                _reason = ai == null ? "body-pending" : null;
                _actionAt = now;
                ai?.GoTo(new Vector3((float)x, (float)y, (float)z), ArriveDistance, _action.Run);
                return CommandResult.Success;
            }
            case "idle":
                _action = NpcAction.Idle;
                _status = NpcActionStatus.Idle;
                _reason = null;
                _actionAt = now;
                ai?.StopCommand();
                return CommandResult.Success;
            case "stop":
                _action = NpcAction.Stop;
                _status = NpcActionStatus.Idle;
                _reason = null;
                _actionAt = now;
                ai?.StopCommand();
                return CommandResult.Success;
            case "wait" when args.Length >= 1:
            {
                var seconds = Convert.ToDouble(args[0]);
                if (!IsFinite(seconds))
                {
                    return CommandResult.Fail("invalid-duration");
                }

                _action = NpcAction.Wait(Math.Clamp(seconds, 0d, 3600d));
                _status = NpcActionStatus.Running;
                _reason = ai == null ? "body-pending" : null;
                _actionAt = now;
                ai?.StopCommand();
                if (_action.Seconds <= 0d) _status = NpcActionStatus.Arrived;
                return CommandResult.Success;
            }
            case "lookAt" when args.Length >= 3:
            {
                var x = Convert.ToDouble(args[0]);
                var y = Convert.ToDouble(args[1]);
                var z = Convert.ToDouble(args[2]);
                if (!IsFinite(x) || !IsFinite(y) || !IsFinite(z))
                {
                    return CommandResult.Fail("invalid-target");
                }

                _action = NpcAction.LookAt(x, y, z);
                _status = ai == null ? NpcActionStatus.Running : NpcActionStatus.Arrived;
                _reason = ai == null ? "body-pending" : null;
                _actionAt = now;
                ai?.LookAtPoint(new Vector3((float)x, (float)y, (float)z));
                return CommandResult.Success;
            }
            case "face" when args.Length >= 1:
            {
                var handle = args[0] as string ?? string.Empty;
                if (string.IsNullOrWhiteSpace(handle))
                {
                    return CommandResult.Fail("invalid-target");
                }

                _action = NpcAction.Face(handle);
                _status = NpcActionStatus.Running;
                _reason = ai == null ? "body-pending" : null;
                _actionAt = now;
                ai?.Face(handle);
                return CommandResult.Success;
            }
            case "pick" when args.Length >= 1:
            {
                var handle = args[0] as string ?? string.Empty;
                if (string.IsNullOrWhiteSpace(handle))
                {
                    return CommandResult.Fail("invalid-target");
                }

                _action = NpcAction.Pick(handle);
                _status = ai == null ? NpcActionStatus.Paused : NpcActionStatus.Running;
                _reason = ai == null ? "body-lost" : null;
                _actionAt = now;
                ai?.StopCommand();
                return CommandResult.Success;
            }
            default:
                return CommandResult.Fail("unknown-action");
        }
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

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
        var stats = obj.Stats;
        if (stats == null && obj.Character != null)
        {
            stats = obj.Character.GetComponent<VjsBodyStats>();
        }

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
            Ready = obj.BodyReady,
            Self = stats?.GetView(),
            Action = BuildActionView(),
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
            var live = scene.FindInstance(zdo.m_uid);
            var pickable = live != null ? live.GetComponent<Pickable>() : prefab.GetComponent<Pickable>();
            entry.Ready = kind != "pickable" || VjsActions.IsPickableReady(pickable);
            entry.Food = kind == "pickable" && VjsActions.IsFoodPickable(pickable);
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
        if (prefab.GetComponent<Pickable>() != null)
        {
            return "pickable";
        }

        if (prefab.GetComponent<TreeBase>() != null)
        {
            return "tree";
        }

        if (prefab.GetComponent<MineRock>() != null || prefab.GetComponent<MineRock5>() != null)
        {
            return "rock";
        }

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

    private static string ActionName(NpcActionKind kind) => kind switch
    {
        NpcActionKind.MoveTo => "moveTo",
        NpcActionKind.Wait => "wait",
        NpcActionKind.LookAt => "lookAt",
        NpcActionKind.Face => "face",
        NpcActionKind.Pick => "pick",
        NpcActionKind.Stop => "stop",
        _ => "idle"
    };

    private static string StatusName(NpcActionStatus status) => status switch
    {
        NpcActionStatus.Running => "running",
        NpcActionStatus.Paused => "paused",
        NpcActionStatus.Arrived => "arrived",
        NpcActionStatus.Failed => "failed",
        NpcActionStatus.Cancelled => "cancelled",
        _ => "idle"
    };
}
