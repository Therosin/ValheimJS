using System;
using System.Collections.Generic;
using System.Linq;
using Jint;
using Jint.Native;
using Jint.Native.Function;
using Jint.Native.Object;
using ValheimJS.Core;
using ValheimJS.Entities;
using ValheimJS.Scripting.Binding;
using ValheimJS.Scripting.Types;
using UnityEngine;
using ValheimJS.World;

namespace ValheimJS.Bindings;

/// <summary>
/// Scripted NPCs: kind registration, spawn/despawn, the per-NPC perception store, intents, durable
/// memory and state. `ValheimBaseNPC` is a native class; this binder owns the JS subclass instances
/// and the host cadence, and publishes perception for the <c>ai</c> binding.
/// </summary>
[JsBinder("npc", Phase = JsBindPhase.Gameplay, Order = 141,
    Description = "Scripted NPC entities: spawn, perception, intents and durable state.")]
internal sealed class NpcBindings : JsBindings
{
    private const int MaxInstancesPerKind = 32;

    private sealed class Blueprint
    {
        internal string Plugin = "";
        internal string Kind = "";
        internal JsCallback Constructor = null!;
        internal NpcDefinition Definition = null!;
        internal Ticker Tick = null!;
        internal Ticker Scan = null!;
        internal readonly Dictionary<string, Instance> Instances = new(StringComparer.Ordinal);
    }

    private sealed class Instance
    {
        internal string Handle = "";
        internal JsValue Object = JsValue.Undefined;
        internal JsCallback? OnTick;
        internal JsCallback? OnSpawn;
        internal JsCallback? OnRehydrate;
        internal JsCallback? OnDespawn;
        internal JsCallback? OnSensed;
        internal JsCallback? OnIntent;
        internal string LastIntent = "idle";
        internal string LastIntentKind = "";
        internal bool Muted;
        internal bool Hydrated;
    }

    private readonly Dictionary<string, Dictionary<string, Blueprint>> _blueprints = new(StringComparer.Ordinal);
    private readonly HashSet<string> _debug = new(StringComparer.Ordinal);
    private JsTypeRegistry? _types;
    private JsNativeType? _npcType;

    public NpcBindings(JsBindingContext context) : base(context)
    {
    }

    public override void Install(JsTypeRegistry types)
    {
        _types = types;
        _npcType = types.Register<BaseNPC>(arguments =>
        {
            var handle = arguments.Length > 0 ? arguments[0].ToString() : string.Empty;
            if (string.IsNullOrEmpty(handle) || WorldManager.Instance == null)
            {
                throw new InvalidOperationException("npc: instance is not bound to an entity");
            }

            var entity = WorldManager.Instance.Resolve(types.CurrentPlugin, handle)
                ?? throw new InvalidOperationException($"npc: unknown handle '{handle}'");
            return new BaseNPC(types.CurrentPlugin, entity.Id, entity.Kind, entity.Name);
        });
    }

    [JsBinding(Scoped = true, Description = "True when the world scene can host NPCs.")]
    public bool Available(string plugin) => WorldManager.Instance?.Available == true;

    [JsBinding(Scoped = true, Description = "Enables host-side NPC tracing: lifecycle, perception and settled intents.")]
    public bool Debug(string plugin, bool enabled)
    {
        if (enabled) _debug.Add(plugin);
        else _debug.Remove(plugin);
        return enabled;
    }

    [JsBinding(Scoped = true, ReturnKind = JsReturnKind.Native, Returns = "new (handle: string) => ValheimBaseNPC",
        Description = "Base constructor for plugin NPC subclasses.")]
    public JsValue Base(string plugin) =>
        _npcType != null ? JsValue.FromObject(Engine, _npcType.Constructor) : JsValue.Null;

    [JsBinding(Scoped = true, Description = "Registers an NPC kind and its plugin subclass constructor.")]
    public void Register(string plugin, string kind, string prefab, double tickMs, JsCallback constructor, JsValue options)
    {
        var settings = options is ObjectInstance optionObject ? optionObject : null;
        var faction = OptionalString(settings, "faction");
        var sensors = Sensors(settings);
        if (!NpcDefinition.TryCreate(kind, prefab, tickMs, faction, sensors, out var definition, out var error))
        {
            throw new InvalidOperationException(error);
        }

        var kinds = Kinds(plugin);
        if (kinds.ContainsKey(kind))
        {
            throw new InvalidOperationException($"npc: kind '{kind}' is already registered");
        }

        var manager = WorldManager.Instance ?? throw new InvalidOperationException("npc: world manager is not running");
        manager.RegisterKind(new WorldKind
        {
            Name = definition.Kind,
            Prefab = NpcPrefabs.PrefabName(definition.Kind),
            TickMs = definition.TickMs,
            RelevancyRadius = 64f,
            Create = obj => new NpcBehavior(obj, definition.Sensors),
            Prepare = () => NpcPrefabs.TryApply(definition)
        });

        kinds[kind] = new Blueprint
        {
            Plugin = plugin,
            Kind = kind,
            Constructor = constructor,
            Definition = definition,
            Tick = new Ticker(definition.TickMs),
            Scan = new Ticker(500d)
        };
    }

    [JsBinding(Scoped = true, ReturnKind = JsReturnKind.Native, Returns = "ValheimBaseNPC | null",
        Description = "Spawns one NPC on the world authority; returns the entity or null.")]
    public JsValue Spawn(string plugin, string kind, float x, float y, float z, [JsNullable] string? name)
    {
        if (!Kinds(plugin).TryGetValue(kind, out var blueprint))
        {
            throw new InvalidOperationException($"npc: unknown kind '{kind}'");
        }

        if (blueprint.Instances.Count >= MaxInstancesPerKind)
        {
            LogWarn($"npc: '{kind}' is at its {MaxInstancesPerKind}-instance limit");
            return JsValue.Null;
        }

        var handle = WorldManager.Instance?.Spawn(plugin, kind, x, y, z, name);
        if (handle == null)
        {
            return JsValue.Null;
        }

        var instance = CreateInstance(blueprint, handle);
        if (instance == null)
        {
            WorldManager.Instance?.Despawn(plugin, handle);
            return JsValue.Null;
        }

        instance.Hydrated = true;
        Invoke(blueprint, instance, instance.OnSpawn, "onSpawn", View(plugin, handle));
        return instance.Object;
    }

    [JsBinding(Scoped = true, Description = "Despawns one of this plugin's NPCs.")]
    public bool Despawn(string plugin, JsValue entity)
    {
        var handle = HandleOf(entity);
        if (handle == null || WorldManager.Instance?.Despawn(plugin, handle) != true)
        {
            return false;
        }

        if (Find(plugin, handle, out var blueprint, out var instance))
        {
            Invoke(blueprint, instance, instance.OnDespawn, "onDespawn");
            blueprint.Instances.Remove(handle);
            AiSenses.Clear(plugin, handle);
        }

        return true;
    }

    [JsBinding(Scoped = true, ReturnKind = JsReturnKind.Native, Returns = "ValheimBaseNPC[]",
        Description = "Live NPCs of one kind owned by this plugin.")]
    public JsValue Handles(string plugin, string kind)
    {
        var values = new List<JsValue>();
        if (Kinds(plugin).TryGetValue(kind, out var blueprint) && WorldManager.Instance != null)
        {
            foreach (var handle in WorldManager.Instance.Handles(plugin, kind))
            {
                var instance = CreateInstance(blueprint, handle);
                if (instance != null)
                {
                    values.Add(instance.Object);
                }
            }
        }

        return JsPayload.Array(Engine, values);
    }

    [JsBinding(Scoped = true, ReturnKind = JsReturnKind.Native, Returns = "NpcView | null",
        Description = "Perception for one NPC (same object onTick receives).")]
    public JsValue Perception(string plugin, JsValue entity)
    {
        var handle = HandleOf(entity);
        return handle == null ? JsValue.Null : View(plugin, handle) ?? JsValue.Null;
    }

    [JsBinding(Scoped = true, ReturnKind = JsReturnKind.Native, Returns = "NpcSensed[]",
        Description = "Queries the NPC's perception store; entities persist as memory after leaving range.")]
    public JsValue Sensed(string plugin, JsValue entity, JsValue options)
    {
        var handle = HandleOf(entity);
        var manager = WorldManager.Instance;
        var obj = handle != null && manager != null ? manager.Resolve(plugin, handle) : null;
        if (handle == null || manager == null || obj == null)
        {
            return JsPayload.Array(Engine, Array.Empty<JsValue?>());
        }

        var query = Query(options);
        var matches = manager.Senses(plugin, handle, query);
        var now = Time.realtimeSinceStartup;
        var values = new List<JsValue?>(matches.Count);
        foreach (var entry in matches)
        {
            values.Add(JsPayload.FromPlain(Engine,
                entry.ToPlain(obj.Position.x, obj.Position.y, obj.Position.z, obj.Yaw, now)));
        }

        return JsPayload.Array(Engine, values);
    }

    [JsBinding(Scoped = true, ReturnKind = JsReturnKind.Native, Returns = "Record<string, unknown>",
        Description = "Persistent plugin-owned memory object for one NPC (survives restarts).")]
    public JsValue Memory(string plugin, JsValue entity)
    {
        var handle = HandleOf(entity);
        var json = handle != null ? WorldManager.Instance?.MemoryGet(plugin, handle) : null;
        return string.IsNullOrEmpty(json) ? JsPayload.Object(Engine).Build() : Json.Parse(Engine, json!);
    }

    [JsBinding(Scoped = true, Description = "Replaces the NPC's persistent memory object (16 KiB JSON limit).")]
    public bool SetMemory(string plugin, JsValue entity, JsValue value)
    {
        if (value is not ObjectInstance)
        {
            throw new ArgumentException("npc.setMemory: value must be an object.");
        }

        var handle = HandleOf(entity);
        if (handle == null)
        {
            return false;
        }

        return WorldManager.Instance?.MemorySet(plugin, handle, Json.Stringify(Engine, value)) == true;
    }

    /// <summary>Reconciles subscriptions and ticks server-side AI on the host cadence.</summary>
    public override void Pump()
    {
        var manager = WorldManager.Instance;
        if (manager == null || !manager.Available)
        {
            return;
        }

        foreach (var blueprint in _blueprints.Values.SelectMany(kinds => kinds.Values))
        {
            Scan(blueprint);
            Tick(blueprint);
        }
    }

    public override void Release(string plugin)
    {
        _blueprints.Remove(plugin);
        _debug.Remove(plugin);
        AiSenses.Clear(plugin);
    }

    public override void Dispose()
    {
        _blueprints.Clear();
        _debug.Clear();
        AiSenses.Clear();
    }

    private void Scan(Blueprint blueprint)
    {
        if (!blueprint.Scan.Due())
        {
            return;
        }

        var manager = WorldManager.Instance;
        if (manager == null)
        {
            return;
        }

        var handles = manager.Handles(blueprint.Plugin, blueprint.Kind);
        var seen = new HashSet<string>(handles, StringComparer.Ordinal);
        foreach (var handle in handles)
        {
            var known = blueprint.Instances.ContainsKey(handle);
            var instance = CreateInstance(blueprint, handle);
            if (instance == null)
            {
                continue;
            }

            if (!known && _debug.Contains(blueprint.Plugin))
            {
                LogInfo($"[{blueprint.Plugin}] [npc] {blueprint.Kind} spawned {handle}");
            }

            if (!known && !GameEnvironment.IsServer())
            {
                Invoke(blueprint, instance, instance.OnSpawn, "onSpawn", View(blueprint.Plugin, handle));
            }

            if (!instance.Hydrated && !GameEnvironment.IsServer() &&
                ZNetScene.instance?.FindInstance(ResolveId(handle)) != null)
            {
                instance.Hydrated = true;
                Invoke(blueprint, instance, instance.OnRehydrate, "onRehydrate", View(blueprint.Plugin, handle));
            }
        }

        foreach (var handle in blueprint.Instances.Keys.ToList())
        {
            if (seen.Contains(handle))
            {
                continue;
            }

            var instance = blueprint.Instances[handle];
            if (_debug.Contains(blueprint.Plugin)) LogInfo($"[{blueprint.Plugin}] [npc] {blueprint.Kind} despawned {handle}");
            Invoke(blueprint, instance, instance.OnDespawn, "onDespawn");
            blueprint.Instances.Remove(handle);
            AiSenses.Clear(blueprint.Plugin, handle);
        }
    }

    private void Tick(Blueprint blueprint)
    {
        if (!GameEnvironment.IsServer() || !blueprint.Tick.Due())
        {
            return;
        }

        var manager = WorldManager.Instance;
        if (manager == null)
        {
            return;
        }

        foreach (var instance in blueprint.Instances.Values.ToList())
        {
            var perception = instance.Muted ? null : PerceptionOf(blueprint.Plugin, instance.Handle);
            var obj = manager.Resolve(blueprint.Plugin, instance.Handle);
            if (perception == null || obj == null)
            {
                continue;
            }

            var now = Time.realtimeSinceStartup;
            AiSenses.Publish(blueprint.Plugin, instance.Handle, perception.ToPlain(obj.Yaw, now));

            foreach (var entry in manager.TakeNewSenses(blueprint.Plugin, instance.Handle))
            {
                if (_debug.Contains(blueprint.Plugin))
                {
                    LogInfo(
                        $"[{blueprint.Plugin}] [npc] {blueprint.Kind} {instance.Handle} sensed " +
                        $"{entry.Name} ({(string.IsNullOrEmpty(entry.Kind) ? entry.Prefab ?? entry.Id : entry.Kind)}) " +
                        $"at {entry.X:0.0},{entry.Y:0.0},{entry.Z:0.0} distance={entry.Distance:0.0} " +
                        $"enemy={entry.Enemy} player={entry.IsPlayer} object={entry.IsObject}");
                }

                Invoke(blueprint, instance, instance.OnSensed, "onSensed",
                    JsPayload.FromPlain(Engine, entry.ToPlain(obj.Position.x, obj.Position.y, obj.Position.z, obj.Yaw, now)));
            }

            ReportIntent(blueprint, instance, perception, obj.Yaw);

            var view = Payload(perception, obj.Yaw);
            if (!instance.Hydrated)
            {
                instance.Hydrated = true;
                if (_debug.Contains(blueprint.Plugin))
                {
                    LogInfo($"[{blueprint.Plugin}] [npc] {blueprint.Kind} {instance.Handle} rehydrated");
                }
                Invoke(blueprint, instance, instance.OnRehydrate, "onRehydrate", view);
            }

            Invoke(blueprint, instance, instance.OnTick, "onTick", view);
        }
    }

    private void ReportIntent(Blueprint blueprint, Instance instance, NpcPerception perception, float yaw)
    {
        var intent = perception.Intent;
        if (intent == null)
        {
            return;
        }

        var status = intent.Status;
        if (_debug.Contains(blueprint.Plugin) &&
            (intent.Kind != instance.LastIntentKind || status != instance.LastIntent))
        {
            var target = intent.X.HasValue
                ? $"{intent.X.Value:0.0},{intent.Y ?? 0d:0.0},{intent.Z ?? 0d:0.0}"
                : "-";
            LogInfo(
                $"[{blueprint.Plugin}] [npc] {blueprint.Kind} {instance.Handle} intent " +
                $"{intent.Kind} {status} reason={intent.Reason ?? "-"} target={target} id={intent.TargetId ?? "-"}");
        }

        instance.LastIntentKind = intent.Kind;
        if (status is "arrived" or "failed")
        {
            if (instance.LastIntent == status)
            {
                return;
            }

            instance.LastIntent = status;
            Invoke(blueprint, instance, instance.OnIntent, "onIntent",
                Payload(perception, yaw), JsPayload.FromPlain(Engine, intent.ToPlain()));
            return;
        }

        instance.LastIntent = status;
    }

    private Instance? CreateInstance(Blueprint blueprint, string handle)
    {
        if (blueprint.Instances.TryGetValue(handle, out var existing))
        {
            return existing;
        }

        if (_types == null)
        {
            return null;
        }

        JsValue value;
        Instance instance;
        var previous = JsContextScope.Enter(Engine, blueprint.Constructor.Context);
        try
        {
            value = Engine.Construct(blueprint.Constructor.Function, JsValue.FromObject(Engine, handle));
            var target = value.AsObject();
            instance = new Instance
            {
                Handle = handle,
                Object = value,
                OnTick = Hook(target, "onTick", blueprint.Kind),
                OnSpawn = Hook(target, "onSpawn", blueprint.Kind),
                OnRehydrate = Hook(target, "onRehydrate", blueprint.Kind),
                OnDespawn = Hook(target, "onDespawn", blueprint.Kind),
                OnSensed = Hook(target, "onSensed", blueprint.Kind),
                OnIntent = Hook(target, "onIntent", blueprint.Kind)
            };
        }
        catch (Exception exception)
        {
            LogError($"[{blueprint.Plugin}] npc '{handle}' construction failed: {Describe(exception)}");
            return null;
        }
        finally
        {
            JsContextScope.Exit(Engine, previous);
        }

        blueprint.Instances[handle] = instance;
        return instance;
    }

    private JsCallback? Hook(Jint.Native.Object.ObjectInstance target, string name, string kind)
    {
        var value = target.Get(name);
        return value is Function function
            ? new JsCallback(Engine, function, $"npc.{kind}.{name}")
            : null;
    }

    private void Invoke(Blueprint blueprint, Instance instance, JsCallback? callback, string hook, params object?[] arguments)
    {
        if (callback == null)
        {
            return;
        }

        try
        {
            callback.InvokeOn(instance.Object, arguments);
        }
        catch (Exception exception)
        {
            instance.Muted = true;
            LogError($"[{blueprint.Plugin}] npc '{instance.Handle}' {hook} threw: {Describe(exception)}");
            if (hook == "onTick")
            {
                WorldManager.Instance?.Command(blueprint.Plugin, instance.Handle, "idle");
            }
        }
    }

    private JsValue? View(string plugin, string handle)
    {
        var perception = PerceptionOf(plugin, handle);
        var obj = WorldManager.Instance?.Resolve(plugin, handle);
        if (perception == null || obj == null)
        {
            return null;
        }

        if (GameEnvironment.IsServer())
        {
            AiSenses.Publish(plugin, handle, perception.ToPlain(obj.Yaw, Time.realtimeSinceStartup));
        }

        return Payload(perception, obj.Yaw);
    }

    private NpcPerception? PerceptionOf(string plugin, string handle)
    {
        var manager = WorldManager.Instance;
        if (manager == null)
        {
            return null;
        }

        if (manager.Snapshot(plugin, handle) is NpcPerception perception)
        {
            return perception;
        }

        var obj = manager.Resolve(plugin, handle);
        if (obj == null)
        {
            return null;
        }

        var zdo = WorldManager.Zdo(handle);
        return new NpcPerception
        {
            Id = obj.Handle,
            Kind = obj.Kind,
            X = zdo?.GetPosition().x ?? 0d,
            Y = zdo?.GetPosition().y ?? 0d,
            Z = zdo?.GetPosition().z ?? 0d,
            Health = NpcBehavior.HealthOf(obj.Id, 0f),
            MaxHealth = NpcBehavior.MaxHealthOf(obj.Id),
            IsAlive = NpcBehavior.IsAlive(obj.Id, NpcBehavior.HealthOf(obj.Id, 0f)),
            Faction = string.Empty,
            Level = 1,
            HomeX = obj.Home.x,
            HomeY = obj.Home.y,
            HomeZ = obj.Home.z
        };
    }

    private JsValue Payload(NpcPerception perception, float yaw) =>
        JsPayload.FromPlain(Engine, perception.ToPlain(yaw, Time.realtimeSinceStartup));

    private static NpcSensors? Sensors(ObjectInstance? options)
    {
        if (options?.Get("perception") is not ObjectInstance perception)
        {
            return null;
        }

        var defaults = new NpcSensors();
        return new NpcSensors
        {
            Radius = (float)Number(perception, "radius", defaults.Radius),
            Max = (int)Number(perception, "max", defaults.Max),
            ViewRange = (float)Number(perception, "viewRange", defaults.ViewRange),
            ViewAngle = (float)Number(perception, "viewAngle", defaults.ViewAngle),
            LineOfSight = Bool(perception, "lineOfSight", defaults.LineOfSight),
            IncludePlayers = Bool(perception, "includePlayers", defaults.IncludePlayers),
            IncludeCreatures = Bool(perception, "includeCreatures", defaults.IncludeCreatures),
            IncludeTamed = Bool(perception, "includeTamed", defaults.IncludeTamed),
            IncludeNpcs = Bool(perception, "includeNpcs", defaults.IncludeNpcs),
            IncludeDead = Bool(perception, "includeDead", defaults.IncludeDead),
            ObjectRadius = (float)Number(perception, "objectRadius", defaults.ObjectRadius),
            ObjectMax = (int)Number(perception, "objectMax", defaults.ObjectMax)
        };
    }

    private static NpcSenseQuery Query(JsValue options)
    {
        var query = new NpcSenseQuery();
        if (options is not ObjectInstance obj)
        {
            return query;
        }

        var id = obj.Get("id");
        if (id.IsString())
        {
            query.Id = id.AsString();
        }

        query.Radius = (float)Number(obj, "radius", query.Radius);
        query.Angle = (float)Number(obj, "angle", query.Angle);
        if (obj.Get("dir") is ObjectInstance dir)
        {
            var x = OptionalNumber(dir, "x");
            var z = OptionalNumber(dir, "z");
            if (x.HasValue && z.HasValue)
            {
                query.DirectionX = x.Value;
                query.DirectionZ = z.Value;
            }
        }

        if (obj.Get("kinds") is JsArray kinds)
        {
            var list = new List<string>();
            for (var index = 0u; index < kinds.Length; index++)
            {
                var item = kinds.Get(index);
                if (item.IsString())
                {
                    list.Add(item.AsString());
                }
            }

            query.Kinds = list;
        }

        query.VisibleOnly = Bool(obj, "visible", query.VisibleOnly);
        query.EnemyOnly = Bool(obj, "enemy", query.EnemyOnly);
        query.PlayersOnly = Bool(obj, "players", query.PlayersOnly);
        query.IncludeRemembered = Bool(obj, "remembered", query.IncludeRemembered);
        query.Max = Math.Clamp((int)Number(obj, "max", query.Max), 1, 64);
        return query;
    }

    private static string? OptionalString(ObjectInstance? options, string name)
    {
        var value = options?.Get(name) ?? JsValue.Undefined;
        if (value.IsUndefined() || value.IsNull())
        {
            return null;
        }

        return value.IsString() ? value.AsString() : throw new ArgumentException($"npc: '{name}' must be a string");
    }

    private static double Number(ObjectInstance options, string name, double fallback)
    {
        var value = options.Get(name);
        if (value.IsUndefined() || value.IsNull())
        {
            return fallback;
        }

        return value.IsNumber() ? value.AsNumber() : throw new ArgumentException($"npc: '{name}' must be a number");
    }

    private static double? OptionalNumber(ObjectInstance options, string name)
    {
        var value = options.Get(name);
        if (value.IsUndefined() || value.IsNull())
        {
            return null;
        }

        return value.IsNumber() ? value.AsNumber() : throw new ArgumentException($"npc: '{name}' must be a number");
    }

    private static bool Bool(ObjectInstance options, string name, bool fallback)
    {
        var value = options.Get(name);
        if (value.IsUndefined() || value.IsNull())
        {
            return fallback;
        }

        return value.IsBoolean() ? value.AsBoolean() : throw new ArgumentException($"npc: '{name}' must be a boolean");
    }

    private Dictionary<string, Blueprint> Kinds(string plugin)
    {
        if (!_blueprints.TryGetValue(plugin, out var kinds))
        {
            kinds = new Dictionary<string, Blueprint>(StringComparer.Ordinal);
            _blueprints[plugin] = kinds;
        }

        return kinds;
    }

    private bool Find(string plugin, string handle, out Blueprint blueprint, out Instance instance)
    {
        if (_blueprints.TryGetValue(plugin, out var kinds))
        {
            foreach (var candidate in kinds.Values)
            {
                if (candidate.Instances.TryGetValue(handle, out instance!))
                {
                    blueprint = candidate;
                    return true;
                }
            }
        }

        blueprint = null!;
        instance = null!;
        return false;
    }

    private static ZDOID ResolveId(string handle) =>
        WorldManager.TryParseHandle(handle, out var id) ? id : ZDOID.None;

    private string? HandleOf(JsValue entity) =>
        _types?.Handle(entity) is BaseNPC npc ? npc.Id : null;

    private static string Describe(Exception exception)
    {
        var inner = exception;
        while (inner.InnerException != null)
        {
            inner = inner.InnerException;
        }

        return inner.GetType().Name + ": " + inner.Message;
    }
}
