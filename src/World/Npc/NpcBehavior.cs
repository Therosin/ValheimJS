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
internal sealed class NpcBehavior : IWorldBehavior, INpcSenses, INpcActions, INpcEvents
{
    private const float ArriveDistance = 0.75f;
    private const float PerceptionCacheSeconds = 0.1f;

    private static readonly int ViewBlockMask = LayerMask.GetMask(
        "Default", "static_solid", "Default_small", "piece", "terrain", "viewblock", "vehicle");

    private readonly NpcSensors _sensors;
    private readonly Dictionary<string, NpcSensedEntity> _entities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NpcSensedEntity> _objects = new(StringComparer.Ordinal);
    private readonly List<NpcSensedEntity> _newEntities = new();
    private readonly List<NpcSensedEntity> _heard = new();
    private readonly List<NpcDamageEvent> _damage = new();
    private Vector3 _origin;
    private float _originYaw;
    private float _now;

    private readonly NpcActionState _state;
    private bool _bound;
    private NpcPerception? _perception;
    private float _perceptionAt = -1f;
    private int _perceptionGeneration = -1;

    private int _lastGeneration;
    private string _boardAction = "";
    private string _boardSelf = "";
    private string _boardCombat = "";
    private double _writtenStartDistance;
    private double _writtenWaitUntil;
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
        if (zdo == null || !zdo.GetString(WorldKeys.ActionKind, out var kind))
        {
            _state = new NpcActionState();
            return;
        }

        var action = kind switch
        {
            "moveTo" => NpcAction.MoveTo(
                zdo.GetFloat(WorldKeys.ActionX, obj.Position.x),
                zdo.GetFloat(WorldKeys.ActionY, obj.Position.y),
                zdo.GetFloat(WorldKeys.ActionZ, obj.Position.z),
                zdo.GetBool(WorldKeys.ActionRun)),
            "lookAt" => NpcAction.LookAt(
                zdo.GetFloat(WorldKeys.ActionX, obj.Position.x),
                zdo.GetFloat(WorldKeys.ActionY, obj.Position.y),
                zdo.GetFloat(WorldKeys.ActionZ, obj.Position.z)),
            "wait" => NpcAction.Wait(zdo.GetFloat(WorldKeys.ActionSeconds, 0f)),
            "face" => NpcAction.Face(zdo.GetString(WorldKeys.ActionTarget, "")),
            "pick" => NpcAction.Pick(zdo.GetString(WorldKeys.ActionTarget, "")),
            "stop" => NpcAction.Stop,
            _ => NpcAction.Idle
        };

        var status = action.Kind is NpcActionKind.Idle or NpcActionKind.Stop
            ? NpcActionStatus.Idle
            : NpcActionStatus.Running;
        _state = new NpcActionState(
            action,
            status,
            null,
            null,
            zdo.GetFloat(WorldKeys.ActionStartDistance, 0f),
            zdo.GetFloat(WorldKeys.ActionWaitUntil, 0f));
    }

    /// <summary>Replays the current intent onto a body that was just attached.</summary>
    public void Bind(WorldObject obj)
    {
        _bound = true;
        var ai = obj.AI;
        if (ai == null)
        {
            return;
        }

        var action = _state.Action;
        switch (action.Kind)
        {
            case NpcActionKind.MoveTo:
                ai.GoTo(new Vector3((float)action.X, (float)action.Y, (float)action.Z),
                    ArriveDistance, action.Run);
                break;
            case NpcActionKind.LookAt:
                ai.LookAtPoint(new Vector3((float)action.X, (float)action.Y, (float)action.Z));
                break;
            case NpcActionKind.Face when !string.IsNullOrWhiteSpace(action.TargetId):
                ai.Face(action.TargetId!);
                break;
            case NpcActionKind.Pick:
                _state.Resume();
                break;
            default:
                ai.StopCommand();
                break;
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

        SyncAction(obj);
        DrainBodyEvents(obj);
        PublishBoard(obj);
    }

    private void DrainBodyEvents(WorldObject obj)
    {
        foreach (var damage in obj.AI?.TakeDamageEvents() ?? Array.Empty<NpcDamageEvent>())
        {
            if (_damage.Count >= 32)
            {
                _damage.RemoveAt(0);
            }

            _damage.Add(damage);
        }
    }

    /// <summary>Advances the intent from the body, the wait deadline or death.</summary>
    private void SyncAction(WorldObject obj)
    {
        if (obj.Character != null && obj.Character.IsDead())
        {
            _state.MarkDead();
            obj.Moving = false;
            return;
        }

        _state.ClearDead();

        if (_state.Action.Kind == NpcActionKind.Wait)
        {
            _state.TickWait(GameNow());
            obj.Moving = false;
            return;
        }

        var ai = obj.AI;
        if (ai == null)
        {
            obj.Moving = false;
            if (_state.IsInFlight)
            {
                _state.Pause(_bound ? "body-lost" : "body-pending");
            }

            return;
        }

        SyncFromBody(obj);
        obj.Moving = ai.IsMoving;

        if (_state.Action.Kind == NpcActionKind.Pick && _state.IsInFlight)
        {
            _state.Resume();
            if (VjsActions.Pick(obj, _state.Action.TargetId ?? "", out var reason))
            {
                _state.Settle(NpcActionStatus.Arrived, null);
            }
            else
            {
                _state.Settle(NpcActionStatus.Failed, reason);
            }
            return;
        }

        if (_state.Action.Kind == NpcActionKind.MoveTo && _state.IsInFlight)
        {
            _state.SetDistance(Horizontal(obj.Position, _state.Action));
        }
    }

    /// <summary>Copies the body's intent status/settlement into the action state.</summary>
    private void SyncFromBody(WorldObject obj)
    {
        var ai = obj.AI;
        if (ai == null || !_state.IsInFlight ||
            _state.Action.Kind is not (NpcActionKind.MoveTo or NpcActionKind.LookAt or NpcActionKind.Face))
        {
            return;
        }

        switch (ai.Status)
        {
            case NpcActionStatus.Arrived:
            case NpcActionStatus.Failed:
                _state.Settle(ai.Status, ai.Reason);
                break;
            case NpcActionStatus.Paused:
                _state.Pause(ai.Reason ?? "combat");
                break;
            default:
                _state.Resume();
                break;
        }
    }

    public IReadOnlyList<NpcActionView> TakeActionSettlements() => _state.TakeSettlements();

    public IReadOnlyList<NpcSensedEntity> TakeHeard()
    {
        if (_heard.Count == 0)
        {
            return Array.Empty<NpcSensedEntity>();
        }

        var result = _heard.ToArray();
        _heard.Clear();
        return result;
    }

    public IReadOnlyList<NpcDamageEvent> TakeDamage()
    {
        if (_damage.Count == 0)
        {
            return Array.Empty<NpcDamageEvent>();
        }

        var result = _damage.ToArray();
        _damage.Clear();
        return result;
    }

    private NpcActionView BuildActionView() => _state.View();

    private void PublishBoard(WorldObject obj)
    {
        var channel = NpcBoards.For(obj.Handle);
        channel.Set("ready", obj.BodyReady);

        var action = BuildActionView();
        var actionKey = $"{action.Seq}:{action.Kind}:{action.Status}:{action.Reason}:{action.Progress}";
        if (actionKey != _boardAction)
        {
            _boardAction = actionKey;
            channel.Set("action", action.ToPlain());
        }

        var combatKey = obj.AI is { Engaged: true } ai ? $"1:{ai.ThreatId}" : "0";
        if (combatKey != _boardCombat)
        {
            _boardCombat = combatKey;
            channel.Set("combat", new Dictionary<string, object?>
            {
                ["engaged"] = obj.AI?.Engaged == true,
                ["threatId"] = obj.AI?.ThreatId
            });
        }

        if (!obj.BodyReady) return;
        var self = obj.Stats?.GetView();
        if (self == null) return;
        var health = self["health"] is float h ? Math.Round(h, 1) : 0d;
        var stamina = self["stamina"] is float s ? Math.Round(s, 1) : 0d;
        var food = self["food"] is System.Collections.ICollection collection ? collection.Count : 0;
        var hungry = self["hungry"] is true;
        var selfKey = $"{health}:{stamina}:{food}:{hungry}";
        if (selfKey == _boardSelf) return;
        _boardSelf = selfKey;
        channel.Set("self", self);
        channel.Set("hungry", hungry);
    }

    private static float GameNow() =>
        ZNet.instance != null ? (float)ZNet.instance.GetTimeSeconds() : Time.time;

    private static float Horizontal(Vector3 position, NpcAction action)
    {
        var dx = position.x - (float)action.X;
        var dz = position.z - (float)action.Z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    public void Write(WorldObject obj, ZDO zdo)
    {
        var action = _state.Action;
        if (!ActionChanged(action))
        {
            return;
        }

        RememberAction(action);
        zdo.Set(WorldKeys.ActionKind, NpcActionState.ActionName(action.Kind));
        zdo.Set(WorldKeys.ActionX, (float)action.X);
        zdo.Set(WorldKeys.ActionY, (float)action.Y);
        zdo.Set(WorldKeys.ActionZ, (float)action.Z);
        zdo.Set(WorldKeys.ActionRun, action.Run);
        zdo.Set(WorldKeys.ActionTarget, action.TargetId ?? string.Empty);
        zdo.Set(WorldKeys.ActionSeconds, (float)action.Seconds);
        zdo.Set(WorldKeys.ActionStartDistance, _state.StartDistance);
        zdo.Set(WorldKeys.ActionWaitUntil, _state.WaitUntil);
        zdo.Set(WorldKeys.Generation, obj.Generation);
    }

    /// <summary>True when the current intent differs from what was last written to the ZDO.</summary>
    private bool ActionChanged(NpcAction action)
    {
        if (_writtenKind != action.Kind)
        {
            return true;
        }

        if (_writtenStartDistance != _state.StartDistance || _writtenWaitUntil != _state.WaitUntil)
        {
            return true;
        }

        return action.Kind switch
        {
            NpcActionKind.MoveTo => _writtenX != action.X || _writtenY != action.Y ||
                                    _writtenZ != action.Z || _writtenRun != action.Run,
            NpcActionKind.LookAt => _writtenX != action.X || _writtenY != action.Y ||
                                    _writtenZ != action.Z,
            NpcActionKind.Wait => _writtenSeconds != action.Seconds,
            NpcActionKind.Face => !string.Equals(_writtenTarget, action.TargetId, StringComparison.Ordinal),
            _ => false
        };
    }

    private void RememberAction(NpcAction action)
    {
        _writtenKind = action.Kind;
        _writtenX = action.X;
        _writtenY = action.Y;
        _writtenZ = action.Z;
        _writtenSeconds = action.Seconds;
        _writtenRun = action.Run;
        _writtenTarget = action.TargetId;
        _writtenStartDistance = _state.StartDistance;
        _writtenWaitUntil = _state.WaitUntil;
    }

    public object? Snapshot(WorldObject obj)
    {
        if (obj.Character != null)
        {
            obj.Position = obj.Character.transform.position;
            obj.Yaw = obj.Character.transform.eulerAngles.y;
        }

        if (obj.Character != null && obj.Character.IsDead())
        {
            _state.MarkDead();
        }
        else
        {
            SyncFromBody(obj);
            if (_state.Action.Kind == NpcActionKind.MoveTo && _state.IsInFlight)
            {
                _state.SetDistance(Horizontal(obj.Position, _state.Action));
            }
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
        SyncFromBody(obj);
        if (command == "attack" && obj.Character is Humanoid attacker)
        {
            VjsInventory.EquipBestFor(attacker, "attack");
        }

        var now = GameNow();
        // A command can replace the intent immediately. Invalidate the perception cache so the
        // caller observes the new action instead of the previous move/arrival snapshot.
        _perceptionAt = -1f;
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

                var action = NpcAction.MoveTo(x, y, z, args.Length > 3 && Convert.ToBoolean(args[3]));
                _state.Issue(action, now, Horizontal(obj.Position, action));
                ai?.GoTo(new Vector3((float)x, (float)y, (float)z), ArriveDistance, action.Run);
                return CommandResult.Success;
            }
            case "idle":
                _state.Issue(NpcAction.Idle, now, 0f);
                ai?.ClearIntent();
                return CommandResult.Success;
            case "stop":
                _state.Issue(NpcAction.Stop, now, 0f);
                ai?.ClearIntent();
                return CommandResult.Success;
            case "wait" when args.Length >= 1:
            {
                var seconds = Convert.ToDouble(args[0]);
                if (!IsFinite(seconds))
                {
                    return CommandResult.Fail("invalid-duration");
                }

                _state.Issue(NpcAction.Wait(Math.Clamp(seconds, 0d, 3600d)), now, 0f);
                ai?.ClearIntent();
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

                _state.Issue(NpcAction.LookAt(x, y, z), now, 0f);
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

                _state.Issue(NpcAction.Face(handle), now, 0f);
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

                _state.Issue(NpcAction.Pick(handle), now, 0f);
                ai?.ClearIntent();
                return CommandResult.Success;
            }
            case "attack" when args.Length >= 1:
            {
                var handle = args[0] as string ?? string.Empty;
                if (!WorldManager.TryParseHandle(handle, out var targetId))
                {
                    return CommandResult.Fail("invalid-target");
                }

                var target = ZNetScene.instance?.FindInstance(targetId)?.GetComponent<Character>();
                if (obj.Character == null)
                {
                    return CommandResult.Fail("body-lost");
                }

                if (target == null)
                {
                    return CommandResult.Fail("target-missing");
                }

                if (target == obj.Character || target.IsDead())
                {
                    return CommandResult.Fail("invalid-target");
                }

                var weapon = (obj.Character as Humanoid)?.GetCurrentWeapon();
                var range = Mathf.Max(2f, weapon != null ? weapon.m_shared.m_aiAttackRange : 2f);
                if (Vector3.Distance(obj.Character.transform.position, target.transform.position) > range)
                {
                    return CommandResult.Fail("out-of-range");
                }

                obj.Character.StartAttack(target, args.Length > 1 && Convert.ToBoolean(args[1]));
                return CommandResult.Success;
            }
            case "jump":
                if (obj.Character == null)
                {
                    return CommandResult.Fail("body-lost");
                }

                obj.Character.Jump(false);
                return CommandResult.Success;
            case "crouch" when args.Length >= 1:
                if (obj.AI == null)
                {
                    return CommandResult.Fail("body-lost");
                }

                obj.AI.SetCrouched(Convert.ToBoolean(args[0]));
                return CommandResult.Success;
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

        var sector = NpcSectorScan.Take(obj.Position);
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
            Swimming = obj.Character != null && obj.Character.IsSwimming(),
            Engaged = obj.AI?.Engaged == true,
            ThreatId = obj.AI?.ThreatId,
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
            Nearby = ScanCharacters(obj, character, faction, isTamed, sector),
            Remembered = Remembered(),
            Objects = ScanObjects(obj, sector)
        };
    }

    private IReadOnlyList<NpcSensedEntity> ScanCharacters(
        WorldObject obj,
        Character? self,
        Character.Faction selfFaction,
        bool selfTamed,
        List<ZDO> objects)
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
            var hearRange = _sensors.Hearing > 0f ? _sensors.Hearing : _sensors.Radius;
            var noise = live != null ? live.GetNoiseRange() : zdo.GetFloat(ZDOVars.s_noise, 0f);
            var audible = obj.Character != null && live != null
                ? BaseAI.CanHearTarget(obj.Character.transform, hearRange, live)
                : NpcAudibility.IsAudible(distance, noise, hearRange, position.y > 3000f);
            var wasAudible = entry.Audible;
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
            entry.Ready = !isDead;
            entry.Noise = noise;
            entry.Audible = audible;
            entry.Visible = visible;
            entry.InRange = true;
            entry.LastSeenAt = _now;
            entry.SeenCount++;
            DescribeCharacter(entry, live);

            if (audible && !wasAudible)
            {
                if (_heard.Count >= 32)
                {
                    _heard.RemoveAt(0);
                }

                _heard.Add(entry);
            }

            nearby.Add(entry);
        }

        foreach (var entry in _entities.Values)
        {
            if (!entry.InRange)
            {
                entry.Audible = false;
            }
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

    public IReadOnlyList<NpcSensedEntity> Find(NpcFindQuery query)
    {
        var matches = new List<NpcSensedEntity>();
        foreach (var entry in _entities.Values)
        {
            if (query.Allows(entry, _origin.x, _origin.z))
            {
                matches.Add(entry);
            }
        }

        foreach (var entry in _objects.Values)
        {
            if (query.Allows(entry, _origin.x, _origin.z))
            {
                matches.Add(entry);
            }
        }

        matches.Sort((left, right) => query.Sort switch
        {
            "health" => right.Health.CompareTo(left.Health),
            "level" => right.Level.CompareTo(left.Level),
            "seenCount" => right.SeenCount.CompareTo(left.SeenCount),
            _ => Distance(left).CompareTo(Distance(right))
        });

        if (query.Skip > 0 && query.Skip < matches.Count)
        {
            matches.RemoveRange(0, query.Skip);
        }
        else if (query.Skip >= matches.Count)
        {
            matches.Clear();
        }

        return matches.Count > query.Max ? matches.GetRange(0, query.Max) : matches;

        double Distance(NpcSensedEntity entry)
        {
            var dx = entry.X - _origin.x;
            var dz = entry.Z - _origin.z;
            return dx * dx + dz * dz;
        }
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

    private IReadOnlyList<NpcSensedEntity> ScanObjects(WorldObject obj, List<ZDO> objects)
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
            entry.ToolTier = prefab.GetComponent<TreeBase>()?.m_minToolTier
                ?? prefab.GetComponent<TreeLog>()?.m_minToolTier
                ?? prefab.GetComponent<MineRock>()?.m_minToolTier
                ?? prefab.GetComponent<MineRock5>()?.m_minToolTier
                ?? 0;
            entry.X = position.x;
            entry.Y = position.y;
            entry.Z = position.z;
            entry.Distance = distance;
            var live = scene.FindInstance(zdo.m_uid);
            var pickable = live != null ? live.GetComponent<Pickable>() : null;
            entry.Ready = kind != "pickable" || VjsActions.IsPickableReady(zdo, pickable, prefab);
            entry.Food = kind == "pickable" &&
                         VjsActions.IsFoodPickable(pickable ?? prefab.GetComponent<Pickable>());
            entry.Visible = true;
            entry.InRange = true;
            entry.LastSeenAt = _now;
            entry.SeenCount++;
            DescribeObject(entry, obj, kind, prefab, live, pickable);
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

    private static void DescribeCharacter(NpcSensedEntity entry, Character? live)
    {
        ResetFacts(entry);
        AddTag(entry, "character");
        AddTag(entry, entry.IsPlayer ? "player" : entry.IsNpc ? "npc" : "creature");
        if (entry.IsTamed) AddTag(entry, "tamed");
        if (entry.Enemy) AddTag(entry, "enemy");
        if (entry.IsDead) AddTag(entry, "dead");
        AddVerb(entry, "attack");
        AddVerb(entry, "face");
        entry.Usable = true;
        entry.Equipment = live is Humanoid humanoid ? EquipmentInfo(humanoid) : null;
    }

    private static NpcEquipmentInfo EquipmentInfo(Humanoid humanoid)
    {
        var equipment = VjsInventory.Equipment(humanoid);
        return new NpcEquipmentInfo
        {
            Right = ItemInfoOrNull(equipment.GetValueOrDefault("right")),
            Left = ItemInfoOrNull(equipment.GetValueOrDefault("left")),
            Helmet = ItemInfoOrNull(equipment.GetValueOrDefault("helmet")),
            Chest = ItemInfoOrNull(equipment.GetValueOrDefault("chest")),
            Legs = ItemInfoOrNull(equipment.GetValueOrDefault("legs")),
            Shoulder = ItemInfoOrNull(equipment.GetValueOrDefault("shoulder")),
            Utility = ItemInfoOrNull(equipment.GetValueOrDefault("utility")),
            Trinket = ItemInfoOrNull(equipment.GetValueOrDefault("trinket"))
        };
    }

    private static NpcItemInfo? ItemInfoOrNull(ItemDrop.ItemData? item) =>
        item?.m_shared == null ? null : ItemInfo(item);

    private void DescribeObject(
        NpcSensedEntity entry,
        WorldObject actor,
        string kind,
        GameObject prefab,
        GameObject? live,
        Pickable? pickable)
    {
        ResetFacts(entry);
        AddTag(entry, kind);

        switch (kind)
        {
            case "tree":
            case "log":
                AddTag(entry, "wood");
                AddTag(entry, "harvestable");
                AddVerb(entry, "harvest");
                AddRequirement(entry, "chop");
                AddDrops(entry, prefab.GetComponent<TreeBase>()?.m_dropWhenDestroyed);
                AddDrops(entry, prefab.GetComponent<TreeLog>()?.m_dropWhenDestroyed);
                break;

            case "rock":
                AddTag(entry, "stone");
                AddTag(entry, "harvestable");
                AddVerb(entry, "harvest");
                AddRequirement(entry, "pickaxe");
                AddDrops(entry, prefab.GetComponent<MineRock>()?.m_dropItems);
                AddDrops(entry, prefab.GetComponent<MineRock5>()?.m_dropItems);
                break;

            case "pickable":
                AddTag(entry, "loot");
                AddVerb(entry, "pick");
                var itemPrefab = (pickable ?? prefab.GetComponent<Pickable>())?.m_itemPrefab;
                if (itemPrefab != null) AddItem(entry, itemPrefab);
                if (entry.Food) AddTag(entry, "food");
                break;

            case "item":
                AddTag(entry, "loot");
                AddTag(entry, "item");
                AddVerb(entry, "pickup");
                AddItem(entry, live != null ? live : prefab);
                break;

            case "container":
                AddTag(entry, "storage");
                AddVerb(entry, "store");
                AddVerb(entry, "take");
                entry.Container = ContainerInfo(entry, live, prefab);
                break;

            case "smelter":
                AddVerb(entry, "addOre");
                AddVerb(entry, "addFuel");
                break;

            case "door":
                AddVerb(entry, "open");
                AddVerb(entry, "close");
                AddVerb(entry, "toggle");
                break;

            case "bed":
                AddTag(entry, "shelter");
                AddVerb(entry, "sleep");
                AddRequirement(entry, "player");
                break;

            case "station":
                AddVerb(entry, "craft");
                AddVerb(entry, "repair");
                AddRequirement(entry, "player");
                break;

            case "cooking":
                AddTag(entry, "station");
                AddVerb(entry, "cook");
                break;

            case "portal":
                AddVerb(entry, "use");
                AddRequirement(entry, "player");
                break;
        }

        entry.Usable = CanUse(actor, entry);
    }

    private static void AddItem(NpcSensedEntity entry, GameObject itemPrefab)
    {
        if (itemPrefab == null)
        {
            return;
        }

        if (!entry.Gives.Contains(itemPrefab.name))
        {
            entry.Gives.Add(itemPrefab.name);
        }

        var data = itemPrefab.GetComponent<ItemDrop>()?.m_itemData;
        var shared = data?.m_shared;
        if (shared == null)
        {
            return;
        }

        entry.Item = ItemInfo(data!);

        var name = itemPrefab.name;
        if (shared.m_food > 0f || shared.m_foodStamina > 0f || shared.m_foodEitr > 0f)
        {
            AddTag(entry, "food");
            if (name.StartsWith("Cooked", StringComparison.OrdinalIgnoreCase))
            {
                AddTag(entry, "cooked");
            }
            else
            {
                AddTag(entry, "raw");
            }
        }

        if (name.Contains("Meat", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Boar", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Deer", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Wolf", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Serpent", StringComparison.OrdinalIgnoreCase))
        {
            AddTag(entry, "meat");
        }

        if (name.Contains("Seed", StringComparison.OrdinalIgnoreCase))
        {
            AddTag(entry, "seed");
        }

        if (name is "Wood" or "FineWood" or "RoundLog" or "ElderBark")
        {
            AddTag(entry, "wood");
        }

        if (name.Contains("Stone", StringComparison.OrdinalIgnoreCase))
        {
            AddTag(entry, "stone");
        }

        if (name.EndsWith("Ore", StringComparison.OrdinalIgnoreCase))
        {
            AddTag(entry, "ore");
        }

        if (name.Contains("Ingot", StringComparison.OrdinalIgnoreCase))
        {
            AddTag(entry, "metal");
        }
    }

    private NpcContainerInfo ContainerInfo(NpcSensedEntity entry, GameObject? live, GameObject prefab)
    {
        if (_now - entry.ContainerScannedAt < 2d && entry.Container != null)
        {
            return entry.Container;
        }

        entry.ContainerScannedAt = _now;
        var container = (live != null ? live.GetComponent<Container>() : null) ??
                        prefab.GetComponent<Container>();
        var inventory = container?.GetInventory();
        var info = new NpcContainerInfo { Available = inventory != null };
        if (inventory == null)
        {
            return info;
        }

        info.Slots = inventory.GetWidth() * inventory.GetHeight();
        info.ItemCount = inventory.NrOfItems();
        info.FreeSlots = Mathf.Max(0, info.Slots - info.ItemCount);
        info.Weight = inventory.GetTotalWeight();
        var bestFoodScore = 0f;
        foreach (var item in inventory.GetAllItems())
        {
            if (item?.m_shared == null) continue;
            AddContainerCategory(info, VjsInventory.Kind(item), item.m_stack);
            var prefabName = VjsInventory.PrefabName(item);
            if (!string.IsNullOrWhiteSpace(prefabName))
            {
                info.Prefabs.TryGetValue(prefabName, out var prefabAmount);
                info.Prefabs[prefabName] = prefabAmount + item.m_stack;
            }

            var name = item.m_shared.m_name;
            if (item.m_shared.m_food > 0f || item.m_shared.m_foodStamina > 0f || item.m_shared.m_foodEitr > 0f)
                AddContainerCategory(info, "food", item.m_stack);
            if (name is "Wood" or "FineWood" or "RoundLog" or "ElderBark")
                AddContainerCategory(info, "wood", item.m_stack);
            if (name.Contains("Stone", StringComparison.OrdinalIgnoreCase))
                AddContainerCategory(info, "stone", item.m_stack);
            if (name.EndsWith("Ore", StringComparison.OrdinalIgnoreCase))
                AddContainerCategory(info, "ore", item.m_stack);
            if (name.Contains("Ingot", StringComparison.OrdinalIgnoreCase))
                AddContainerCategory(info, "metal", item.m_stack);
            if (name.Contains("Seed", StringComparison.OrdinalIgnoreCase))
                AddContainerCategory(info, "seed", item.m_stack);

            var foodScore = item.m_shared.m_food + item.m_shared.m_foodStamina + item.m_shared.m_foodEitr + item.m_shared.m_foodRegen;
            if (foodScore > bestFoodScore)
            {
                bestFoodScore = foodScore;
                info.BestFood = ItemInfo(item);
            }
        }

        entry.Container = info;
        return info;
    }

    private static void AddContainerCategory(NpcContainerInfo info, string kind, int amount)
    {
        info.Categories.TryGetValue(kind, out var count);
        info.Categories[kind] = count + 1;
        info.Amounts.TryGetValue(kind, out var total);
        info.Amounts[kind] = total + amount;
    }

    private static NpcItemInfo ItemInfo(ItemDrop.ItemData data)
    {
        var info = new NpcItemInfo
        {
            Type = VjsInventory.Kind(data),
            TypeName = data.m_shared.m_itemType.ToString(),
            EquipSlot = EquipSlot(data.m_shared.m_itemType),
            Quality = data.m_quality,
            MaxQuality = data.m_shared.m_maxQuality,
            Stack = data.m_stack,
            MaxStack = data.m_shared.m_maxStackSize,
            Durability = data.m_durability,
            MaxDurability = data.GetMaxDurability(),
            DurabilityRatio = data.GetDurabilityPercentage(),
            Weight = data.GetWeight(),
            UnitWeight = data.GetNonStackedWeight(),
            Armor = data.GetArmor(),
            ToolTier = data.m_shared.m_toolTier,
            CanRepair = data.m_shared.m_canBeReparied
        };

        var damage = data.GetDamage();
        AddDamage(info, "blunt", damage.m_blunt);
        AddDamage(info, "slash", damage.m_slash);
        AddDamage(info, "pierce", damage.m_pierce);
        AddDamage(info, "chop", damage.m_chop);
        AddDamage(info, "pickaxe", damage.m_pickaxe);
        AddDamage(info, "fire", damage.m_fire);
        AddDamage(info, "frost", damage.m_frost);
        AddDamage(info, "lightning", damage.m_lightning);
        AddDamage(info, "poison", damage.m_poison);
        AddDamage(info, "spirit", damage.m_spirit);

        if (data.m_shared.m_food > 0f || data.m_shared.m_foodStamina > 0f || data.m_shared.m_foodEitr > 0f)
        {
            info.IsFood = true;
            info.Food["health"] = data.m_shared.m_food;
            info.Food["stamina"] = data.m_shared.m_foodStamina;
            info.Food["eitr"] = data.m_shared.m_foodEitr;
            info.Food["burnTime"] = data.m_shared.m_foodBurnTime;
            info.Food["regen"] = data.m_shared.m_foodRegen;
        }

        return info;
    }

    private static void AddDamage(NpcItemInfo info, string key, float value)
    {
        if (value > 0f)
        {
            info.Damage[key] = value;
        }
    }

    private static string EquipSlot(ItemDrop.ItemData.ItemType type) => type switch
    {
        ItemDrop.ItemData.ItemType.Helmet => "head",
        ItemDrop.ItemData.ItemType.Chest => "chest",
        ItemDrop.ItemData.ItemType.Legs => "legs",
        ItemDrop.ItemData.ItemType.Shoulder => "shoulder",
        ItemDrop.ItemData.ItemType.Utility => "utility",
        ItemDrop.ItemData.ItemType.Trinket => "trinket",
        ItemDrop.ItemData.ItemType.Shield => "left",
        ItemDrop.ItemData.ItemType.Ammo => "ammo",
        ItemDrop.ItemData.ItemType.OneHandedWeapon => "right",
        ItemDrop.ItemData.ItemType.TwoHandedWeapon => "right",
        ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft => "left",
        ItemDrop.ItemData.ItemType.Bow => "right",
        ItemDrop.ItemData.ItemType.Torch => "right",
        ItemDrop.ItemData.ItemType.Tool => "right",
        _ => ""
    };

    private static void AddDrops(NpcSensedEntity entry, DropTable? table)
    {
        if (table == null)
        {
            return;
        }

        foreach (var drop in table.GetDropList())
        {
            if (drop != null)
            {
                AddItem(entry, drop);
            }
        }
    }

    private static bool CanUse(WorldObject actor, NpcSensedEntity entry)
    {
        if (entry.Requires.Count == 0)
        {
            return true;
        }

        if (entry.Requires.Contains("player"))
        {
            return false;
        }

        if (actor.Character is not Humanoid humanoid)
        {
            return false;
        }

        var tool = humanoid.RightItem;
        if (tool == null || tool.m_shared.m_useDurability && tool.m_durability <= 0f)
        {
            return false;
        }

        if (tool.m_shared.m_toolTier < entry.ToolTier)
        {
            return false;
        }

        var damage = tool.GetDamage();
        foreach (var requirement in entry.Requires)
        {
            switch (requirement)
            {
                case "chop" when damage.m_chop <= 0f:
                case "pickaxe" when damage.m_pickaxe <= 0f:
                    return false;
                case "chop":
                case "pickaxe":
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    private static void ResetFacts(NpcSensedEntity entry)
    {
        entry.Tags.Clear();
        entry.Verbs.Clear();
        entry.Requires.Clear();
        entry.Gives.Clear();
        entry.Usable = false;
    }

    private static void AddTag(NpcSensedEntity entry, string tag)
    {
        if (!entry.Tags.Contains(tag))
        {
            entry.Tags.Add(tag);
        }
    }

    private static void AddVerb(NpcSensedEntity entry, string verb)
    {
        if (!entry.Verbs.Contains(verb))
        {
            entry.Verbs.Add(verb);
        }
    }

    private static void AddRequirement(NpcSensedEntity entry, string requirement)
    {
        if (!entry.Requires.Contains(requirement))
        {
            entry.Requires.Add(requirement);
        }
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

        if (prefab.GetComponent<TreeLog>() != null)
        {
            return "log";
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

        if (prefab.GetComponent<CookingStation>() != null)
        {
            return "cooking";
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
}
