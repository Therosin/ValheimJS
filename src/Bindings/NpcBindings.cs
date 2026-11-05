using System;
using System.Collections.Generic;
using System.Linq;
using Jint;
using Jint.Native;
using Jint.Native.Function;
using ValheimJS.Entities;
using ValheimJS.Scripting.Binding;
using ValheimJS.Scripting.Types;

namespace ValheimJS.Bindings;

/// <summary>
/// Scripted NPCs: kind registration, spawn/despawn, per-tick perception, intents and durable
/// state. `ValheimBaseNPC` is a native class; this binder owns the JS subclass instances and the
/// host cadence.
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
        internal bool Muted;
        internal bool Hydrated;
    }

    private readonly Dictionary<string, Dictionary<string, Blueprint>> _blueprints = new(StringComparer.Ordinal);
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
            if (string.IsNullOrEmpty(handle) || NpcWorld.Instance == null)
            {
                throw new InvalidOperationException("npc: instance is not bound to an entity");
            }

            return NpcWorld.Instance.Resolve(types.CurrentPlugin, handle)
                ?? throw new InvalidOperationException($"npc: unknown handle '{handle}'");
        });
    }

    [JsBinding(Scoped = true, Description = "True when the world scene can host NPCs.")]
    public bool Available(string plugin) => NpcWorld.Instance?.Available == true;

    [JsBinding(Scoped = true, ReturnKind = JsReturnKind.Native, Returns = "new (handle: string) => ValheimBaseNPC",
        Description = "Base constructor for plugin NPC subclasses.")]
    public JsValue Base(string plugin) =>
        _npcType != null ? JsValue.FromObject(Engine, _npcType.Constructor) : JsValue.Null;

    [JsBinding(Scoped = true, Description = "Registers an NPC kind and its plugin subclass constructor.")]
    public void Register(string plugin, string kind, string prefab, double tickMs, JsCallback constructor)
    {
        if (!NpcDefinition.TryCreate(kind, prefab, tickMs, out var definition, out var error))
        {
            throw new InvalidOperationException(error);
        }

        var kinds = Kinds(plugin);
        if (kinds.ContainsKey(kind))
        {
            throw new InvalidOperationException($"npc: kind '{kind}' is already registered");
        }

        if (NpcWorld.Instance == null || !NpcWorld.Instance.Register(plugin, kind, prefab, definition.TickMs))
        {
            throw new InvalidOperationException($"npc: kind '{kind}' could not be registered");
        }

        kinds[kind] = new Blueprint
        {
            Plugin = plugin,
            Kind = kind,
            Constructor = constructor,
            Definition = definition,
            Tick = new Ticker(definition.TickMs),
            Scan = new Ticker(10_000d)
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

        var handle = NpcWorld.Instance?.Spawn(plugin, kind, x, y, z, name);
        if (handle == null)
        {
            return JsValue.Null;
        }

        var instance = CreateInstance(blueprint, handle);
        if (instance == null)
        {
            NpcWorld.Instance?.Despawn(plugin, handle);
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
        if (handle == null || NpcWorld.Instance?.Despawn(plugin, handle) != true)
        {
            return false;
        }

        if (Find(plugin, handle, out var blueprint, out var instance))
        {
            Invoke(blueprint, instance, instance.OnDespawn, "onDespawn");
            blueprint.Instances.Remove(handle);
        }

        return true;
    }

    [JsBinding(Scoped = true, ReturnKind = JsReturnKind.Native, Returns = "ValheimBaseNPC[]",
        Description = "Live NPCs of one kind owned by this plugin.")]
    public JsValue Handles(string plugin, string kind)
    {
        var values = new List<JsValue>();
        if (Kinds(plugin).TryGetValue(kind, out var blueprint) && NpcWorld.Instance != null)
        {
            foreach (var handle in NpcWorld.Instance.Handles(plugin, kind))
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

    /// <summary>Scans, hydrates and ticks registered kinds. Runs on the host tick.</summary>
    public override void Pump()
    {
        var world = NpcWorld.Instance;
        if (world == null || !world.Available)
        {
            return;
        }

        foreach (var blueprint in _blueprints.Values.SelectMany(kinds => kinds.Values))
        {
            Scan(blueprint);
            Tick(blueprint);
        }
    }

    public override void Release(string plugin) => _blueprints.Remove(plugin);

    public override void Dispose() => _blueprints.Clear();

    private void Scan(Blueprint blueprint)
    {
        if (!blueprint.Scan.Due())
        {
            return;
        }

        var world = NpcWorld.Instance;
        if (world == null)
        {
            return;
        }

        var handles = world.Handles(blueprint.Plugin, blueprint.Kind);
        var seen = new HashSet<string>(handles, StringComparer.Ordinal);
        foreach (var handle in handles)
        {
            CreateInstance(blueprint, handle);
        }

        foreach (var handle in blueprint.Instances.Keys.ToList())
        {
            if (seen.Contains(handle))
            {
                continue;
            }

            var instance = blueprint.Instances[handle];
            Invoke(blueprint, instance, instance.OnDespawn, "onDespawn");
            blueprint.Instances.Remove(handle);
        }
    }

    private void Tick(Blueprint blueprint)
    {
        if (!blueprint.Tick.Due())
        {
            return;
        }

        foreach (var instance in blueprint.Instances.Values.ToList())
        {
            var view = instance.Muted ? null : View(blueprint.Plugin, instance.Handle);
            if (view == null)
            {
                continue;
            }

            if (!instance.Hydrated)
            {
                instance.Hydrated = true;
                Invoke(blueprint, instance, instance.OnRehydrate, "onRehydrate", view);
            }

            Invoke(blueprint, instance, instance.OnTick, "onTick", view);
        }
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
        try
        {
            value = Engine.Construct(blueprint.Constructor.Function, JsValue.FromObject(Engine, handle));
        }
        catch (Exception exception)
        {
            LogError($"[{blueprint.Plugin}] npc '{handle}' construction failed: {Describe(exception)}");
            return null;
        }

        var target = value.AsObject();
        var instance = new Instance
        {
            Handle = handle,
            Object = value,
            OnTick = Hook(target, "onTick", blueprint.Kind),
            OnSpawn = Hook(target, "onSpawn", blueprint.Kind),
            OnRehydrate = Hook(target, "onRehydrate", blueprint.Kind),
            OnDespawn = Hook(target, "onDespawn", blueprint.Kind)
        };

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
            if (hook == "onTick" && _types?.Handle(instance.Object) is BaseNPC npc)
            {
                NpcWorld.Instance?.ApplyIntent(blueprint.Plugin, npc.ZdoId, NpcIntent.Idle);
            }
        }
    }

    private JsValue? View(string plugin, string handle)
    {
        var perception = NpcWorld.Instance?.Perception(plugin, handle);
        return perception == null ? null : Payload(perception);
    }

    private JsValue Payload(NpcPerception perception)
    {
        var view = JsPayload.Object(Engine)
            .Set("id", perception.Id)
            .Set("kind", perception.Kind)
            .Set("x", perception.X)
            .Set("y", perception.Y)
            .Set("z", perception.Z)
            .Set("health", perception.Health)
            .Set("maxHealth", perception.MaxHealth);

        if (perception.LastIntentKind == null)
        {
            view.SetNull("lastIntent");
        }
        else
        {
            var last = JsPayload.Object(Engine)
                .Set("kind", perception.LastIntentKind == NpcIntentKind.MoveTo ? "moveTo" : "idle")
                .Set("ok", perception.LastIntentOk == true);
            if (perception.LastIntentReason != null)
            {
                last.Set("reason", perception.LastIntentReason);
            }

            view.Set("lastIntent", last);
        }

        var nearby = perception.Nearby.Select(entry => JsPayload.Object(Engine)
            .Set("id", entry.Id)
            .Set("prefab", entry.Prefab)
            .Set("name", entry.Name)
            .Set("x", entry.X)
            .Set("y", entry.Y)
            .Set("z", entry.Z)
            .Set("distance", entry.Distance)
            .Set("health", entry.Health)
            .Set("isPlayer", entry.IsPlayer)
            .Build());

        view.Set("nearby", nearby);
        return view.Build();
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

    private string? HandleOf(JsValue entity)
    {
        if (_types?.Handle(entity) is BaseNPC npc)
        {
            return npc.Id;
        }

        return null;
    }

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
