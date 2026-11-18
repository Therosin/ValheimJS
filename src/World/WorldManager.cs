using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;
using ValheimJS.Core;
using ValheimJS.Entities;
using ValheimJS.Networking;

namespace ValheimJS.World;

/// <summary>
/// Authority-owned registry and simulator for world objects. The server is both brain and body:
/// it ticks behaviours and writes position/rotation/state into ZDOs; clients only mirror
/// relevance metadata and render engine-instantiated copies.
/// </summary>
internal sealed class WorldManager : IDisposable
{
    private readonly Core.Networking _network;
    private readonly NetworkScope _scope;
    private readonly NetworkChannel _channel;
    private readonly Dictionary<string, WorldObject> _objects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WorldKind> _kinds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WorldObject> _subscribed = new(StringComparer.Ordinal);
    private readonly Dictionary<long, HashSet<string>> _peerSets = new();
    private readonly Dictionary<long, float> _peerReady = new();
    private readonly List<(string Handle, float X, float Z)> _relevancyObjects = new();
    private const float ReadyTtl = 10f;
    private const float ReadyHeartbeat = 3f;
    private float _nextReady;
    private bool _readyInFlight;
    private bool _readyAcked;
    private float _nextRehydrate;
    private float _nextRelevancy;
    private float _nextCleanup;
    private float _nextSync;
    private bool _syncInFlight;
    private bool _synced;
    private bool _disposed;
    private bool _pluginsLoaded;
    private static readonly FieldInfo? ObjectsField = typeof(ZDOMan).GetField(
        "m_objectsByID",
        BindingFlags.Instance | BindingFlags.NonPublic);
    /// <summary>
    /// Pre-marker NPC prefabs. New ZDOs carry <c>vjs.plugin</c>/<c>vjs.kind</c>, so only the
    /// historical npc-demo needs a hard-coded fallback; hashes cannot reveal a name prefix.
    /// </summary>
    private static readonly HashSet<int> LegacyPrefabHashes = new()
    {
        "vjs_npc_wanderer".GetStableHashCode()
    };

    internal static WorldManager? Instance { get; private set; }

    internal WorldManager(Core.Networking networking)
    {
        _network = networking;
        _scope = networking.OpenScope();
        _channel = _scope.Channel(WorldKeys.Channel, "world");
        _channel.OnJson("relevance", OnRelevance);
        _channel.OnJson("entity", OnEntity);
        _channel.HandleJson("sync", HandleSync);
        _channel.HandleJson("ready", HandleReady);
        Instance = this;
    }

    internal bool Available => ZDOMan.instance != null && ZNetScene.instance != null;

    internal bool Authority => GameEnvironment.IsServer();

    internal void RegisterKind(WorldKind kind)
    {
        if (string.IsNullOrEmpty(kind.Name))
        {
            throw new ArgumentException("world kind: name is required");
        }

        if (string.IsNullOrEmpty(kind.Plugin))
        {
            throw new ArgumentException($"world kind '{kind.Name}': plugin is required");
        }

        if (_kinds.TryGetValue(kind.Name, out var existing))
        {
            throw new ArgumentException(
                $"world kind '{kind.Name}' is already registered by '{existing.Plugin}'");
        }

        _kinds[kind.Name] = kind;
        try
        {
            kind.Prepare?.Invoke();
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning($"world kind '{kind.Name}' prepare failed: {exception.Message}");
        }
    }

    internal void UnregisterKind(string plugin, string kind)
    {
        if (_kinds.TryGetValue(kind, out var info) &&
            string.Equals(info.Plugin, plugin, StringComparison.Ordinal))
        {
            _kinds.Remove(kind);
        }
    }

    /// <summary>Called after the JS host has loaded all plugin definitions for this session.</summary>
    internal void PluginsLoaded()
    {
        _pluginsLoaded = true;
        _nextCleanup = 0f;
    }

    internal void ApplyPendingPrefabs()
    {
        if (!Available)
        {
            return;
        }

        foreach (var kind in _kinds.Values)
        {
            try
            {
                kind.Prepare?.Invoke();
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"world kind '{kind.Name}' prepare failed: {exception.Message}");
            }
        }
    }

    internal string? Spawn(string plugin, string kind, float x, float y, float z, string? name)
    {
        if (!Authority)
        {
            Plugin.Logger.LogWarning("world spawn refused: no authority");
            return null;
        }

        if (!_kinds.TryGetValue(kind, out var kindInfo) ||
            !string.Equals(kindInfo.Plugin, plugin, StringComparison.Ordinal))
        {
            Plugin.Logger.LogWarning($"world spawn refused: unknown kind '{kind}' for plugin '{plugin}'");
            return null;
        }

        kindInfo.Prepare?.Invoke();
        var scene = ZNetScene.instance;
        var prefab = scene?.GetPrefab(kindInfo.Prefab);
        if (prefab == null)
        {
            Plugin.Logger.LogWarning($"world spawn refused: prefab '{kindInfo.Prefab}' is not registered");
            return null;
        }

        var position = GroundPosition(new Vector3(x, y, z));
        if (!scene!.IsAreaReady(position))
        {
            Plugin.Logger.LogWarning($"world spawn refused: area not ready at {position.x:0.0},{position.z:0.0}");
            return null;
        }

        var instance = UnityEngine.Object.Instantiate(prefab, position, Quaternion.identity);
        var view = instance.GetComponent<ZNetView>();
        var zdo = view != null ? view.GetZDO() : null;
        var character = instance.GetComponent<Character>();
        var ai = instance.GetComponent<VjsNpcAI>();
        var stats = instance.GetComponent<VjsBodyStats>();
        if (view == null || zdo == null || character == null || ai == null)
        {
            UnityEngine.Object.Destroy(instance);
            Plugin.Logger.LogWarning($"world spawn refused: '{kindInfo.Prefab}' did not produce a native body");
            return null;
        }

        zdo.Persistent = true;
        character.m_name = name ?? kind;
        zdo.SetPrefab(prefab.name.GetStableHashCode());
        zdo.Set(WorldKeys.Sim, "server");
        zdo.Set(WorldKeys.Plugin, plugin);
        zdo.Set(WorldKeys.Kind, kind);
        zdo.Set(WorldKeys.Name, name ?? kind);
        zdo.Set(WorldKeys.HomeX, position.x);
        zdo.Set(WorldKeys.HomeY, position.y);
        zdo.Set(WorldKeys.HomeZ, position.z);
        zdo.Set(WorldKeys.Speed, 0f);
        zdo.Set(WorldKeys.Moving, false);
        zdo.Set(WorldKeys.Generation, 0);
        zdo.SetRotation(Quaternion.identity);

        var handle = zdo.m_uid.ToString();
        var obj = new WorldObject
        {
            Id = zdo.m_uid,
            Handle = handle,
            Kind = kind,
            Plugin = plugin,
            Name = name ?? kind,
            Position = character.transform.position,
            Home = position,
            View = view,
            Character = character,
            AI = ai,
            Stats = stats,
            KindInfo = kindInfo
        };
        obj.Behavior = kindInfo.Create(obj);
        _objects[handle] = obj;
        return handle;
    }

    internal bool Despawn(string plugin, string handle)
    {
        if (!Authority)
        {
            return false;
        }

        if (!_objects.TryGetValue(handle, out var obj) ||
            !string.Equals(obj.Plugin, plugin, StringComparison.Ordinal))
        {
            return false;
        }

        var zdo = obj.Zdo;
        if (zdo != null)
        {
            if (!zdo.IsOwner())
            {
                zdo.SetOwner(ZDOMan.GetSessionID());
            }

            ZDOMan.instance?.DestroyZDO(zdo);
        }

        _objects.Remove(handle);
        NpcBoards.Remove(handle);
        return true;
    }

    internal IReadOnlyList<string> Handles(string plugin, string kind)
    {
        var source = Authority ? _objects.Values : _subscribed.Values;
        return source
            .Where(obj => string.Equals(obj.Plugin, plugin, StringComparison.Ordinal) &&
                          string.Equals(obj.Kind, kind, StringComparison.Ordinal))
            .Select(obj => obj.Handle)
            .ToList();
    }

    internal WorldObject? Resolve(string plugin, string handle)
    {
        WorldObject? obj;
        if (!_objects.TryGetValue(handle, out obj) && !_subscribed.TryGetValue(handle, out obj))
        {
            return null;
        }

        return string.Equals(obj.Plugin, plugin, StringComparison.Ordinal) ? obj : null;
    }

    internal CommandResult Command(string plugin, string handle, string command, params object?[] args)
    {
        if (!Authority)
        {
            return CommandResult.Fail("not-authority");
        }

        var obj = Resolve(plugin, handle);
        return obj?.Behavior.Command(obj, command, args) ?? CommandResult.Fail("unknown-handle");
    }

    internal object? Snapshot(string plugin, string handle)
    {
        if (!Authority)
        {
            return null;
        }

        var obj = Resolve(plugin, handle);
        return obj?.Behavior.Snapshot(obj);
    }

    internal void Tick(float deltaSeconds)
    {
        if (_disposed)
        {
            return;
        }

        if (Authority)
        {
            ServerTick(deltaSeconds);
        }
        else
        {
            ClientTick();
        }
    }

    internal void ReclaimOwnership()
    {
        if (!Authority)
        {
            return;
        }

        var session = ZDOMan.GetSessionID();
        foreach (var obj in _objects.Values)
        {
            var zdo = obj.Zdo;
            if (zdo != null && !zdo.IsOwner())
            {
                zdo.SetOwner(session);
            }
        }
    }

    /// <summary>
    /// Restores vanilla ownership distribution. The server points its scene reference at a player
    /// so NPC bodies instantiate on a dedicated server; that also makes its own release pass claim
    /// every nearby ZDO before the peer pass runs, leaving interactive objects server-owned. Hand
    /// nearby non-NPC objects to the nearby player (where interaction RPCs have a local player)
    /// and keep simulated NPCs server-authoritative.
    /// </summary>
    internal void RebalanceOwnership()
    {
        if (!Authority)
        {
            return;
        }

        ReclaimOwnership();

        var net = ZNet.instance;
        var manager = ZDOMan.instance;
        if (net == null || manager == null || ZNetScene.instance == null)
        {
            return;
        }

        var session = ZDOMan.GetSessionID();
        var synced = net.GetSyncedSimulationDistance();
        var distance = new SimulationDistance(synced.NearSimulationDistance, 0, synced.IsClassic);
        var objects = new List<ZDO>();

        foreach (var peer in net.GetPeers())
        {
            if (peer == null || peer.m_server || !peer.IsReady())
            {
                continue;
            }

            var reference = PeerPosition(peer);
            if (!Usable(reference))
            {
                continue;
            }

            Vector2s zone = ZoneSystem.GetZone(reference);
            objects.Clear();
            manager.FindSectorObjects(zone, distance, objects);
            foreach (var zdo in objects)
            {
                if (!zdo.Persistent)
                {
                    continue;
                }

                if (IsValheimJsObject(zdo, zdo.GetPrefab()))
                {
                    continue;
                }

                var owner = zdo.GetOwner();
                if (owner == peer.m_uid)
                {
                    continue;
                }

                if (owner != 0 && owner != session && IsPeerActive(zdo.GetPosition(), owner))
                {
                    continue;
                }

                if (!ZNetScene.InActiveArea(zdo.GetPosition(), zone))
                {
                    continue;
                }

                zdo.SetOwner(peer.m_uid);
            }
        }
    }

    private static bool IsPeerActive(Vector3 point, long uid)
    {
        var net = ZNet.instance;
        if (net == null)
        {
            return false;
        }

        if (uid == ZDOMan.GetSessionID())
        {
            return ZNetScene.InActiveArea(point, net.GetReferencePosition());
        }

        var peer = net.GetPeer(uid);
        return peer != null && ZNetScene.InActiveArea(point, peer.GetRefPos());
    }

    internal string? StateGet(string plugin, string handle, string key)
    {
        if (!ValidStateKey(key) || Resolve(plugin, handle) == null)
        {
            return null;
        }

        var zdo = Zdo(handle);
        return zdo != null && zdo.GetString(WorldKeys.StatePrefix + key, out var value) ? value : null;
    }

    internal bool StateSet(string plugin, string handle, string key, string value)
    {
        if (!ValidStateKey(key))
        {
            throw new ArgumentException("world state: invalid key");
        }

        var obj = Resolve(plugin, handle);
        var zdo = obj?.Zdo;
        if (!Authority || zdo == null)
        {
            return false;
        }

        EnsureOwned(zdo);
        zdo.Set(WorldKeys.StatePrefix + key, value ?? string.Empty);
        AddIndexKey(zdo, key);
        return true;
    }

    internal bool StateRemove(string plugin, string handle, string key)
    {
        if (!ValidStateKey(key))
        {
            throw new ArgumentException("world state: invalid key");
        }

        var obj = Resolve(plugin, handle);
        var zdo = obj?.Zdo;
        if (!Authority || zdo == null)
        {
            return false;
        }

        EnsureOwned(zdo);
        var removed = zdo.RemoveString((WorldKeys.StatePrefix + key).GetStableHashCode());
        RemoveIndexKey(zdo, key);
        return removed;
    }

    internal IReadOnlyList<NpcSensedEntity> Senses(string plugin, string handle, NpcSenseQuery query)
    {
        var obj = Resolve(plugin, handle);
        if (obj == null || obj.Behavior is not INpcSenses senses)
        {
            return Array.Empty<NpcSensedEntity>();
        }

        obj.Behavior.Snapshot(obj);
        return senses.Senses(query);
    }

    /// <summary>Declarative query over one NPC's perception store.</summary>
    internal IReadOnlyList<NpcSensedEntity> Find(string plugin, string handle, NpcFindQuery query)
    {
        var obj = Resolve(plugin, handle);
        if (obj == null || obj.Behavior is not INpcSenses senses)
        {
            return Array.Empty<NpcSensedEntity>();
        }

        obj.Behavior.Snapshot(obj);
        return senses.Find(query);
    }

    internal IReadOnlyList<NpcSensedEntity> TakeNewSenses(string plugin, string handle)
    {
        var obj = Resolve(plugin, handle);
        return obj?.Behavior is INpcSenses senses ? senses.TakeNewSenses() : Array.Empty<NpcSensedEntity>();
    }

    /// <summary>Drains audible-enter events for one NPC.</summary>
    internal IReadOnlyList<NpcSensedEntity> TakeHeard(string plugin, string handle)
    {
        var obj = Resolve(plugin, handle);
        return obj?.Behavior is INpcEvents events ? events.TakeHeard() : Array.Empty<NpcSensedEntity>();
    }

    /// <summary>Drains native damage events for one NPC.</summary>
    internal IReadOnlyList<NpcDamageEvent> TakeDamage(string plugin, string handle)
    {
        var obj = Resolve(plugin, handle);
        return obj?.Behavior is INpcEvents events ? events.TakeDamage() : Array.Empty<NpcDamageEvent>();
    }

    /// <summary>Drains settled action outcomes for one NPC, exactly once.</summary>
    internal IReadOnlyList<NpcActionView> TakeActionSettlements(string plugin, string handle)
    {
        var obj = Resolve(plugin, handle);
        return obj?.Behavior is INpcActions actions
            ? actions.TakeActionSettlements()
            : Array.Empty<NpcActionView>();
    }

    internal string? MemoryGet(string plugin, string handle)
    {
        if (Resolve(plugin, handle) == null)
        {
            return null;
        }

        var zdo = Zdo(handle);
        return zdo != null && zdo.GetString(WorldKeys.Memory, out var value) ? value : null;
    }

    internal bool MemorySet(string plugin, string handle, string json)
    {
        if (json.Length > 16384)
        {
            throw new ArgumentException("npc memory: JSON exceeds 16 KiB");
        }

        var obj = Resolve(plugin, handle);
        var zdo = obj?.Zdo;
        if (!Authority || zdo == null)
        {
            return false;
        }

        EnsureOwned(zdo);
        zdo.Set(WorldKeys.Memory, json);
        return true;
    }

    internal string[] StateKeys(string plugin, string handle)
    {
        var obj = Resolve(plugin, handle);
        var zdo = obj?.Zdo;
        if (zdo == null || !zdo.GetString(WorldKeys.StateIndex, out var index) || string.IsNullOrEmpty(index))
        {
            return Array.Empty<string>();
        }

        return index.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
    }

    internal static ZDO? Zdo(string handle)
    {
        return TryParseHandle(handle, out var id) ? ZDOMan.instance?.GetZDO(id) : null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _objects.Clear();
        _subscribed.Clear();
        _kinds.Clear();
        NpcBoards.Clear();
        _peerSets.Clear();
        _scope.Dispose();
        if (ReferenceEquals(Instance, this))
        {
            Instance = null;
        }
    }

    private void ServerTick(float deltaSeconds)
    {
        if (!Available)
        {
            return;
        }

        SyncReference();
        var now = Time.realtimeSinceStartup;
        if (now >= _nextCleanup)
        {
            _nextCleanup = now + 30f;
            CleanupOrphanedObjects();
        }

        if (now >= _nextRehydrate)
        {
            _nextRehydrate = now + 10f;
            Rehydrate();
        }

        foreach (var obj in _objects.Values.ToList())
        {
            if (obj.Zdo == null)
            {
                continue;
            }

            if (obj.Character == null)
            {
                obj.BodyBound = false;
                Relink(obj);
            }

            if (!obj.BodyBound && obj.BodyReady)
            {
                obj.BodyBound = true;
                obj.Behavior.Bind(obj);
            }
            else if (obj.BodyBound && !obj.BodyReady)
            {
                obj.BodyBound = false;
            }

            obj.TickAccumulator += deltaSeconds * 1000d;
            if (obj.TickAccumulator >= obj.KindInfo.TickMs)
            {
                var step = (float)(obj.TickAccumulator / 1000d);
                obj.TickAccumulator = 0d;
                obj.Behavior.Tick(obj, step);
            }

            if (now - obj.LastWrite >= 0.2f)
            {
                WriteObject(obj);
            }
        }

        if (now >= _nextRelevancy)
        {
            _nextRelevancy = now + 1f;
            TickRelevancy();
        }
    }

    private void ClientTick()
    {
        if (!_network.Available || _network.ServerPeer == 0 || !ClientReady())
        {
            _synced = false;
            _readyInFlight = false;
            _readyAcked = false;
            _nextReady = 0f;
            return;
        }

        var now = Time.realtimeSinceStartup;
        if (!_readyAcked && !_readyInFlight)
        {
            _readyInFlight = true;
            _nextReady = now + ReadyHeartbeat;
            _channel.RequestJson(_network.ServerPeer, "ready", "{}", OnReadyReply, 5000);
        }
        else if (_readyAcked && now >= _nextReady)
        {
            _nextReady = now + ReadyHeartbeat;
            _channel.EmitJson(_network.ServerPeer, "ready", "{}");
        }

        if (_syncInFlight || now < _nextSync)
        {
            return;
        }

        _syncInFlight = true;
        _nextSync = now + (_synced ? 30f : 5f);
        _channel.RequestJson(_network.ServerPeer, "sync", "{}", OnSync, 5000);
    }

    private void OnReadyReply(string? error, string? json)
    {
        _readyInFlight = false;
        _readyAcked = string.IsNullOrEmpty(error);
        if (!string.IsNullOrEmpty(error))
        {
            Plugin.Logger.LogWarning("world ready report failed: " + error);
        }
    }

    private string? HandleReady(NetworkMessage message, string json)
    {
        if (!Authority) return null;
        var peer = ZNet.instance?.GetPeer(message.Sender);
        if (peer == null || peer.m_server) return null;
        _peerReady[message.Sender] = Time.realtimeSinceStartup;
        return "{}";
    }

    /// <summary>True when a connected client has reported that its own world is playable.</summary>
    internal bool PeerReady(long uid)
    {
        if (!_peerReady.TryGetValue(uid, out var at)) return false;
        if (Time.realtimeSinceStartup - at <= ReadyTtl) return true;
        _peerReady.Remove(uid);
        return false;
    }

    /// <summary>Server-side alias: is this player actually in-game (not merely connected)?</summary>
    internal bool ClientReady(long uid) => PeerReady(uid);

    /// <summary>
    /// A connected peer is not enough: the client can still be on the loading screen while the
    /// server is already reachable. True once the local character exists and teleport/load
    /// transitions have settled. Dedicated servers have no local player and return false.
    /// </summary>
    internal static bool ClientReady() =>
        GameEnvironment.WorldReady &&
        ZoneSystem.instance != null &&
        ZoneSystem.instance.IsActiveAreaLoaded() &&
        Player.m_localPlayer != null &&
        !Player.m_localPlayer.IsTeleporting();

    private void OnSync(string? error, string? json)
    {
        _syncInFlight = false;
        if (!string.IsNullOrEmpty(error))
        {
            _synced = false;
            Plugin.Logger.LogWarning("world sync failed: " + error);
            return;
        }

        if (string.IsNullOrEmpty(json))
        {
            _synced = false;
            return;
        }

        try
        {
            var message = NetworkJson.Read<WorldRelevanceMessage>(json);
            _network.Post(() => ApplyRelevance(message));
            _synced = true;
        }
        catch (Exception exception)
        {
            _synced = false;
            Plugin.Logger.LogWarning("world sync failed: " + exception.Message);
        }
    }

    private string? HandleSync(NetworkMessage message, string json)
    {
        if (!Authority)
        {
            return null;
        }

        var peer = ZNet.instance?.GetPeers()?.FirstOrDefault(candidate => candidate != null && candidate.m_uid == message.Sender);
        if (peer == null || peer.m_server)
        {
            return NetworkJson.Write(new WorldRelevanceMessage());
        }

        var reference = PeerPosition(peer);
        if (reference == Vector3.zero)
        {
            return NetworkJson.Write(new WorldRelevanceMessage());
        }

        var add = _objects.Values
            .Where(obj => WorldRelevancy.IsRelevant(obj.Position.x, obj.Position.z, reference.x, reference.z,
                obj.KindInfo.RelevancyRadius, false))
            .Select(ToInfo)
            .ToArray();
        return NetworkJson.Write(new WorldRelevanceMessage { Add = add });
    }

    private void OnRelevance(NetworkMessage message, string json)
    {
        if (Authority)
        {
            return;
        }

        try
        {
            var relevance = NetworkJson.Read<WorldRelevanceMessage>(json);
            _network.Post(() => ApplyRelevance(relevance));
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning("world relevance ignored: " + exception.Message);
        }
    }

    private void OnEntity(NetworkMessage message, string json)
    {
        if (Authority)
        {
            return;
        }

        try
        {
            var entity = NetworkJson.Read<WorldEntityMessage>(json);
            if (entity.Entity == null)
            {
                return;
            }

            _network.Post(() =>
            {
                if (string.Equals(entity.Op, "despawn", StringComparison.Ordinal))
                {
                    _subscribed.Remove(entity.Entity.Id);
                }
                else
                {
                    UpsertSubscribed(entity.Entity);
                }
            });
        }
        catch (Exception exception)
        {
            Plugin.Logger.LogWarning("world entity ignored: " + exception.Message);
        }
    }

    private void ApplyRelevance(WorldRelevanceMessage message)
    {
        if (message.Remove != null)
        {
            foreach (var handle in message.Remove)
            {
                _subscribed.Remove(handle);
            }
        }

        if (message.Add != null)
        {
            foreach (var info in message.Add)
            {
                UpsertSubscribed(info);
            }
        }
    }

    private void UpsertSubscribed(WorldEntityInfo info)
    {
        if (info == null || string.IsNullOrEmpty(info.Id) || !TryParseHandle(info.Id, out var id))
        {
            return;
        }

        _subscribed[info.Id] = new WorldObject
        {
            Id = id,
            Handle = info.Id,
            Kind = info.Kind,
            Plugin = info.Plugin,
            Name = info.Name
        };
    }

    private void TickRelevancy()
    {
        var net = ZNet.instance;
        if (net == null)
        {
            return;
        }

        var peers = net.GetPeers();
        if (peers == null)
        {
            return;
        }

        var active = new HashSet<long>();
        _relevancyObjects.Clear();
        foreach (var obj in _objects.Values)
        {
            _relevancyObjects.Add((obj.Handle, obj.Position.x, obj.Position.z));
        }

        foreach (var peer in peers)
        {
            if (peer == null || peer.m_server || !peer.IsReady())
            {
                continue;
            }

            active.Add(peer.m_uid);
            if (!_peerSets.TryGetValue(peer.m_uid, out var previous))
            {
                previous = new HashSet<string>(StringComparer.Ordinal);
                _peerSets[peer.m_uid] = previous;
            }

            var reference = PeerPosition(peer);
            if (reference == Vector3.zero)
            {
                continue;
            }

            var next = new HashSet<string>(StringComparer.Ordinal);
            var add = new List<WorldEntityInfo>();
            var remove = new List<string>();
            foreach (var item in _relevancyObjects)
            {
                var obj = _objects[item.Handle];
                if (WorldRelevancy.IsRelevant(item.X, item.Z, reference.x, reference.z,
                        obj.KindInfo.RelevancyRadius, previous.Contains(item.Handle)))
                {
                    next.Add(item.Handle);
                    if (previous.Add(item.Handle))
                    {
                        add.Add(ToInfo(obj));
                    }
                }
            }

            foreach (var handle in previous)
            {
                if (!next.Contains(handle))
                {
                    remove.Add(handle);
                }
            }

            previous.Clear();
            previous.UnionWith(next);
            if (add.Count == 0 && remove.Count == 0)
            {
                continue;
            }

            _channel.EmitJson(peer.m_uid, "relevance", NetworkJson.Write(new WorldRelevanceMessage
            {
                Add = add.ToArray(),
                Remove = remove.ToArray()
            }));
        }

        foreach (var stale in _peerSets.Keys.Where(uid => !active.Contains(uid)).ToList())
        {
            _peerSets.Remove(stale);
        }

        foreach (var stale in _peerReady.Keys.Where(uid => !active.Contains(uid)).ToList())
        {
            _peerReady.Remove(stale);
        }
    }

    private static Vector3 PeerPosition(ZNetPeer peer)
    {
        var reference = peer.GetRefPos();
        if (reference == Vector3.zero)
        {
            var character = ZDOMan.instance?.GetZDO(peer.m_characterID);
            if (character != null)
            {
                reference = character.GetPosition();
            }
        }

        return reference;
    }

    /// <summary>
    /// Removes VJS-marked ZDOs whose owning plugin/kind is no longer registered. This runs only
    /// on the authority after plugins load and periodically afterwards. A prefab hash alone
    /// cannot be reversed into a <c>vjs_</c> name, so ownership is deliberately carried in the
    /// ZDO (`vjs.sim`, `vjs.plugin`, `vjs.kind`).
    /// </summary>
    /// <summary>
    /// Dedicated servers do not call Player.SetLocalPlayer, so ZNet never gets a reference
    /// position and ZNetScene only loads the origin sector. Point the server's scene reference at
    /// the player (body or ZDO) so native bodies are instantiated around connected clients. Called
    /// from the server tick and from the ZNetScene hook that runs immediately before the
    /// create/destroy pass — the hook is what makes the reference valid on the exact frame that
    /// decides which ZDOs get bodies.
    /// </summary>
    internal void SyncReference()
    {
        if (!Authority)
        {
            return;
        }

        var net = ZNet.instance;
        if (net == null || !TryReference(net, out var reference))
        {
            return;
        }

        net.SetReferencePosition(reference);
    }

    private static bool TryReference(ZNet net, out Vector3 reference)
    {
        // Peers carry the client's actual player position; on a dedicated server this is the
        // only signal that exists before any body has been instantiated.
        foreach (var peer in net.GetPeers())
        {
            if (peer == null || peer.m_server || !peer.IsReady())
            {
                continue;
            }

            var candidate = PeerPosition(peer);
            if (Usable(candidate))
            {
                reference = candidate;
                return true;
            }
        }

        foreach (var character in Character.GetAllCharacters())
        {
            if (character == null || !character.IsPlayer() || character.IsDead())
            {
                continue;
            }

            var candidate = character.transform.position;
            if (Usable(candidate))
            {
                reference = candidate;
                return true;
            }
        }

        // A player's ZDO is synced before its body is instantiated, so it is a fresh reference
        // even while the scene is empty. Without this a dedicated server cannot bootstrap.
        foreach (var zdo in net.GetAllCharacterZDOS())
        {
            if (zdo == null)
            {
                continue;
            }

            var candidate = zdo.GetPosition();
            if (Usable(candidate))
            {
                reference = candidate;
                return true;
            }
        }

        reference = Vector3.zero;
        return false;
    }

    /// <summary>Rejects the far-away sentinel vanilla uses for "no position".</summary>
    private static bool Usable(Vector3 position) =>
        Mathf.Abs(position.x) < 100000f && Mathf.Abs(position.z) < 100000f;

    /// <summary>Lifts a spawn point to the local terrain surface without lowering it.</summary>
    private static Vector3 GroundPosition(Vector3 position)
    {
        var ground = ZoneSystem.instance != null ? ZoneSystem.instance.GetGroundHeight(position) : position.y;
        return new Vector3(position.x, Mathf.Max(position.y, ground + 0.05f), position.z);
    }

    /// <summary>Reattaches a body that ZNetScene re-instantiated, replaying the held intent once.</summary>
    private static void Relink(WorldObject obj)
    {
        var scene = ZNetScene.instance;
        if (scene == null || !TryParseHandle(obj.Handle, out var id))
        {
            return;
        }

        var instance = scene.FindInstance(id);
        if (instance == null)
        {
            return;
        }

        obj.View = instance.GetComponent<ZNetView>();
        obj.Character = instance.GetComponent<Character>();
        obj.AI = instance.GetComponent<VjsNpcAI>();
        obj.Stats = instance.GetComponent<VjsBodyStats>();
        if (obj.Character != null)
        {
            obj.Position = obj.Character.transform.position;
        }
    }

    private void CleanupOrphanedObjects()
    {
        if (!_pluginsLoaded)
        {
            return;
        }

        var manager = ZDOMan.instance;
        var scene = ZNetScene.instance;
        if (manager == null || scene == null || ObjectsField == null ||
            ObjectsField.GetValue(manager) is not IDictionary objects)
        {
            return;
        }

        var removed = 0;
        foreach (var value in objects.Values.Cast<object>().ToList())
        {
            if (value is not ZDO zdo || zdo.GetPrefab() == 0)
            {
                continue;
            }

            var hash = zdo.GetPrefab();
            if (!IsValheimJsObject(zdo, hash) || IsActiveKind(zdo, hash))
            {
                continue;
            }

            if (!zdo.IsOwner())
            {
                zdo.SetOwner(ZDOMan.GetSessionID());
            }

            var cleanupHandle = zdo.m_uid.ToString();
            _objects.Remove(cleanupHandle);
            NpcBoards.Remove(cleanupHandle);
            manager.DestroyZDO(zdo);
            removed++;
        }

        if (removed > 0)
        {
            Plugin.Logger.LogWarning($"world cleanup: removed {removed} orphaned ValheimJS object(s)");
        }
    }

    private bool IsActiveKind(ZDO zdo, int hash)
    {
        var plugin = zdo.GetString(WorldKeys.Plugin, "");
        var kind = zdo.GetString(WorldKeys.Kind, "");
        if (plugin.Length == 0 || kind.Length == 0 ||
            !_kinds.TryGetValue(kind, out var info) ||
            !string.Equals(info.Plugin, plugin, StringComparison.Ordinal))
        {
            return false;
        }

        return info.Prefab.GetStableHashCode() == hash;
    }

    private static bool IsValheimJsObject(ZDO zdo, int hash) =>
        string.Equals(zdo.GetString(WorldKeys.Sim, ""), "server", StringComparison.Ordinal) ||
        zdo.GetString(WorldKeys.Plugin, "").Length > 0 ||
        LegacyPrefabHashes.Contains(hash);

    private void Rehydrate()
    {
        var zdoManager = ZDOMan.instance;
        var scene = ZNetScene.instance;
        if (zdoManager == null || scene == null)
        {
            return;
        }

        foreach (var kind in _kinds.Values)
        {
            var prefab = scene.GetPrefab(kind.Prefab);
            if (prefab == null)
            {
                continue;
            }

            var found = new List<ZDO>();
            var index = 0;
            while (!zdoManager.GetAllZDOsWithPrefabIterative(kind.Prefab, found, ref index))
            {
            }

            foreach (var zdo in found)
            {
                if (!string.Equals(zdo.GetString(WorldKeys.Sim, ""), "server", StringComparison.Ordinal) ||
                    !string.Equals(zdo.GetString(WorldKeys.Kind, ""), kind.Name, StringComparison.Ordinal))
                {
                    continue;
                }

                var handle = zdo.m_uid.ToString();
                if (_objects.TryGetValue(handle, out var existing))
                {
                    if (existing.Character == null || existing.Character.gameObject == null)
                    {
                        Relink(existing);
                    }

                    continue;
                }

                var instance = scene.FindInstance(zdo.m_uid);
                var view = instance != null ? instance.GetComponent<ZNetView>() : null;
                var character = instance != null ? instance.GetComponent<Character>() : null;
                var ai = instance != null ? instance.GetComponent<VjsNpcAI>() : null;
                var stats = instance != null ? instance.GetComponent<VjsBodyStats>() : null;
                var position = character != null ? character.transform.position : zdo.GetPosition();
                if (character != null)
                {
                    character.m_name = zdo.GetString(WorldKeys.Name, kind.Name);
                }
                var obj = new WorldObject
                {
                    Id = zdo.m_uid,
                    Handle = handle,
                    Kind = kind.Name,
                    Plugin = zdo.GetString(WorldKeys.Plugin, ""),
                    Name = zdo.GetString(WorldKeys.Name, kind.Name),
                    Position = position,
                    Home = new Vector3(
                        zdo.GetFloat(WorldKeys.HomeX, position.x),
                        zdo.GetFloat(WorldKeys.HomeY, position.y),
                        zdo.GetFloat(WorldKeys.HomeZ, position.z)),
                    View = view,
                    Character = character,
                    AI = ai,
                    Stats = stats,
                    Generation = zdo.GetInt(WorldKeys.Generation, 0),
                    KindInfo = kind
                };
                obj.Behavior = kind.Create(obj);
                _objects[handle] = obj;
            }
        }
    }

    private void WriteObject(WorldObject obj)
    {
        var zdo = obj.Zdo;
        if (zdo == null)
        {
            return;
        }

        if (!zdo.IsOwner())
        {
            zdo.SetOwner(ZDOMan.GetSessionID());
            return;
        }

        if (obj.Character != null)
        {
            obj.Position = obj.Character.transform.position;
            obj.Yaw = obj.Character.transform.eulerAngles.y;
            obj.Speed = obj.Character.GetVelocity().magnitude;
        }
        else
        {
            zdo.SetPosition(obj.Position);
            zdo.SetRotation(Quaternion.Euler(0f, obj.Yaw, 0f));
        }

        zdo.Set(WorldKeys.Speed, obj.Speed);
        zdo.Set(WorldKeys.Moving, obj.Moving);
        obj.LastWrite = Time.realtimeSinceStartup;
        obj.Behavior.Write(obj, zdo);
    }

    private static WorldEntityInfo ToInfo(WorldObject obj) => new()
    {
        Id = obj.Handle,
        Kind = obj.Kind,
        Plugin = obj.Plugin,
        Name = obj.Name
    };

    private static void EnsureOwned(ZDO zdo)
    {
        if (!zdo.IsOwner())
        {
            zdo.SetOwner(ZDOMan.GetSessionID());
        }
    }

    private static bool ValidStateKey(string key) =>
        !string.IsNullOrEmpty(key) && System.Text.RegularExpressions.Regex.IsMatch(key, "^[A-Za-z0-9_.-]{1,48}$");

    private static void AddIndexKey(ZDO zdo, string key)
    {
        var keys = new List<string>();
        if (zdo.GetString(WorldKeys.StateIndex, out var index) && !string.IsNullOrEmpty(index))
        {
            keys.AddRange(index.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
        }

        if (!keys.Contains(key, StringComparer.Ordinal))
        {
            keys.Add(key);
            zdo.Set(WorldKeys.StateIndex, string.Join(",", keys));
        }
    }

    private static void RemoveIndexKey(ZDO zdo, string key)
    {
        if (!zdo.GetString(WorldKeys.StateIndex, out var index) || string.IsNullOrEmpty(index))
        {
            return;
        }

        var keys = index.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(value => !string.Equals(value, key, StringComparison.Ordinal))
            .ToList();
        zdo.Set(WorldKeys.StateIndex, string.Join(",", keys));
    }

    internal static bool TryParseHandle(string handle, out ZDOID id)
    {
        id = ZDOID.None;
        var separator = handle.IndexOf(':');
        if (separator <= 0)
        {
            return false;
        }

        if (!long.TryParse(handle.AsSpan(0, separator), NumberStyles.Integer, CultureInfo.InvariantCulture, out var user) ||
            !uint.TryParse(handle.AsSpan(separator + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        id = new ZDOID(user, value);
        return true;
    }
}
