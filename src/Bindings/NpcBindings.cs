using System;
using System.Collections.Generic;
using System.Linq;
using Jint;
using Jint.Native;
using Jint.Native.Function;
using Jint.Native.Object;
using ValheimJS.AI;
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
        internal JsCallback? OnHeard;
        internal JsCallback? OnDamaged;
        internal JsCallback? OnAction;
        internal readonly Dictionary<string, int> Failures = new(StringComparer.Ordinal);
        internal readonly HashSet<string> DisabledHooks = new(StringComparer.Ordinal);
        internal bool Hydrated;
        internal bool PendingSpawn;
    }

    private readonly Dictionary<string, Dictionary<string, Blueprint>> _blueprints = new(StringComparer.Ordinal);
    private readonly HashSet<string> _debug = new(StringComparer.Ordinal);
    private JsTypeRegistry? _types;
    private JsNativeType? _npcType;
    private JsNativeType? _inventoryType;

    public NpcBindings(JsBindingContext context) : base(context)
    {
        AiNative.Resolve = ResolveAiAction;
    }

    internal static AiNativeResult ResolveAiAction(
        string plugin,
        string handle,
        string nodeId,
        string name,
        IReadOnlyDictionary<string, object?> args,
        IDictionary<string, object?> state)
    {
        if (name is "findObject" or "findObjects")
        {
            return ResolveFind(plugin, handle, name, args);
        }

        if (name is "moveTo" or "wait" or "lookAt" or "face")
        {
            return ResolveIntent(plugin, handle, nodeId, name, args, state);
        }

        if (name is "harvest" or "pick" or "pickup" or "take" or "store" or "withdraw" or
            "interact" or "cook" or "use")
        {
            return ResolveInteraction(plugin, handle, name, args);
        }

        if (name is "attack" or "jump" or "stop" or "idle" or "crouch")
        {
            return ResolveCommand(plugin, handle, name, args);
        }

        return AiNativeResult.NotHandled;
    }

    private static AiNativeResult ResolveIntent(
        string plugin,
        string handle,
        string nodeId,
        string name,
        IReadOnlyDictionary<string, object?> args,
        IDictionary<string, object?> state)
    {
        var manager = WorldManager.Instance;
        var obj = manager?.Resolve(plugin, handle);
        if (manager == null || obj == null)
        {
            return AiNativeResult.Fail("no-world");
        }

        var key = "intent:" + nodeId;
        if (state.TryGetValue(key, out var tracked) && tracked is long seq)
        {
            return ObserveIntent(manager, plugin, handle, key, seq, state);
        }

        CommandResult result;
        switch (name)
        {
            case "moveTo":
            {
                var position = ReadPosition(args);
                if (position == null)
                {
                    return AiNativeResult.Fail("target-required");
                }

                result = manager.Command(
                    plugin,
                    handle,
                    "moveTo",
                    position.Value.X,
                    position.Value.Y,
                    position.Value.Z,
                    ArgBool(args, "run") ?? false);
                break;
            }

            case "wait":
            {
                var seconds = ArgDouble(args, "seconds") ?? ArgDouble(args, "wait");
                if (seconds == null)
                {
                    return AiNativeResult.Fail("seconds-required");
                }

                result = manager.Command(plugin, handle, "wait", seconds.Value);
                break;
            }

            case "lookAt":
            {
                var position = ReadPosition(args);
                if (position == null)
                {
                    return AiNativeResult.Fail("target-required");
                }

                result = manager.Command(plugin, handle, "lookAt", position.Value.X, position.Value.Y, position.Value.Z);
                break;
            }

            default:
            {
                var target = ArgTarget(args, "target") ?? ArgTarget(args, "id");
                if (string.IsNullOrWhiteSpace(target))
                {
                    return AiNativeResult.Fail("target-required");
                }

                result = manager.Command(plugin, handle, "face", target);
                break;
            }
        }

        if (!result.Ok)
        {
            return AiNativeResult.Fail(result.Reason ?? "rejected");
        }

        var perception = manager.Snapshot(plugin, handle) as NpcPerception;
        var action = perception?.Action;
        if (action == null || action.Seq <= 0)
        {
            return AiNativeResult.Fail("no-action");
        }

        if (action.Status == "arrived")
        {
            return AiNativeResult.Ok(action.ToPlain());
        }

        if (action.Status is "failed" or "cancelled")
        {
            return AiNativeResult.Fail(action.Reason ?? action.Status);
        }

        state[key] = action.Seq;
        return AiNativeResult.Pending(action.Status == "paused");
    }

    private static AiNativeResult ObserveIntent(
        WorldManager manager,
        string plugin,
        string handle,
        string key,
        long seq,
        IDictionary<string, object?> state)
    {
        var perception = manager.Snapshot(plugin, handle) as NpcPerception;
        var action = perception?.Action;
        if (action == null || action.Seq != seq)
        {
            state.Remove(key);
            return AiNativeResult.Fail("superseded");
        }

        switch (action.Status)
        {
            case "arrived":
                state.Remove(key);
                return AiNativeResult.Ok(action.ToPlain());
            case "failed":
            case "cancelled":
                state.Remove(key);
                return AiNativeResult.Fail(action.Reason ?? action.Status);
            default:
                return AiNativeResult.Pending(action.Status == "paused");
        }
    }

    private static (double X, double Y, double Z)? ReadPosition(IReadOnlyDictionary<string, object?> args)
    {
        if (args.TryGetValue("target", out var value) ||
            args.TryGetValue("pos", out value) ||
            args.TryGetValue("position", out value))
        {
            if (value is IReadOnlyDictionary<string, object?> map)
            {
                if (map.TryGetValue("pos", out var nested) && nested is IReadOnlyDictionary<string, object?> posMap)
                {
                    map = posMap;
                }

                var x = ArgDouble(map, "x");
                var y = ArgDouble(map, "y");
                var z = ArgDouble(map, "z");
                if (x.HasValue && y.HasValue && z.HasValue)
                {
                    return (x.Value, y.Value, z.Value);
                }
            }
        }

        var directX = ArgDouble(args, "x");
        var directY = ArgDouble(args, "y");
        var directZ = ArgDouble(args, "z");
        return directX.HasValue && directY.HasValue && directZ.HasValue
            ? (directX.Value, directY.Value, directZ.Value)
            : null;
    }

    private static AiNativeResult ResolveFind(
        string plugin,
        string handle,
        string name,
        IReadOnlyDictionary<string, object?> args)
    {
        var manager = WorldManager.Instance;
        var obj = manager?.Resolve(plugin, handle);
        if (manager == null || obj == null)
        {
            return AiNativeResult.Fail();
        }

        var kind = args.TryGetValue("kind", out var kindValue) ? kindValue as string : null;
        IReadOnlyDictionary<string, object?>? query = null;
        if (args.TryGetValue("query", out var queryValue) &&
            queryValue is IReadOnlyDictionary<string, object?> map)
        {
            query = map;
        }

        var find = NpcFindQuery.FromPlain(query, kind);
        if (name == "findObject")
        {
            find.Max = 1;
        }

        var matches = manager.Find(plugin, handle, find);
        var now = Time.realtimeSinceStartup;
        if (name == "findObject")
        {
            return matches.Count == 0
                ? AiNativeResult.Fail()
                : AiNativeResult.Ok(matches[0].ToPlain(obj.Position.x, obj.Position.y, obj.Position.z, obj.Yaw, now));
        }

        var list = new List<object?>(matches.Count);
        foreach (var entry in matches)
        {
            list.Add(entry.ToPlain(obj.Position.x, obj.Position.y, obj.Position.z, obj.Yaw, now));
        }

        return AiNativeResult.Ok(list);
    }

    private static AiNativeResult ResolveInteraction(
        string plugin,
        string handle,
        string name,
        IReadOnlyDictionary<string, object?> args)
    {
        var manager = WorldManager.Instance;
        var obj = manager?.Resolve(plugin, handle);
        if (manager == null || obj == null)
        {
            return AiNativeResult.Fail();
        }

        var target = ArgTarget(args, "target") ?? ArgTarget(args, "id");
        if (string.IsNullOrWhiteSpace(target))
        {
            return AiNativeResult.Fail("target-required");
        }

        var verb = name switch
        {
            "take" => NpcInteraction.Inspect(obj, target).Kind == "item" ? "pickup" : "take",
            "withdraw" => "take",
            "pick" => "pick",
            "pickup" => "pickup",
            "harvest" => "harvest",
            "store" => "store",
            "cook" => "cook",
            "use" => "use",
            _ => ArgString(args, "verb")
        };

        var options = new NpcInteractOptions
        {
            Verb = verb,
            Prefab = ArgString(args, "prefab"),
            Name = ArgString(args, "item") ?? ArgString(args, "name"),
            Count = Math.Max(1, ArgInt(args, "count") ?? 1),
            Repeat = ArgBool(args, "repeat") ?? false,
            Alt = ArgBool(args, "alt") ?? false
        };

        if (args.TryGetValue("slot", out var slot) && slot is IReadOnlyDictionary<string, object?> slotMap)
        {
            options.X = ArgInt(slotMap, "x");
            options.Y = ArgInt(slotMap, "y");
        }

        var result = NpcInteraction.Interact(obj, target, options);
        return result.Ok
            ? AiNativeResult.Ok(result.ToPlain())
            : AiNativeResult.Fail(result.Reason ?? "rejected");
    }

    private static AiNativeResult ResolveCommand(
        string plugin,
        string handle,
        string name,
        IReadOnlyDictionary<string, object?> args)
    {
        var manager = WorldManager.Instance;
        if (manager?.Resolve(plugin, handle) == null)
        {
            return AiNativeResult.Fail("no-world");
        }

        var result = name switch
        {
            "attack" => manager.Command(
                plugin,
                handle,
                "attack",
                ArgTarget(args, "target") ?? ArgTarget(args, "id") ?? string.Empty,
                ArgBool(args, "secondary") ?? false),
            "jump" => manager.Command(plugin, handle, "jump"),
            "stop" => manager.Command(plugin, handle, "stop"),
            "idle" => manager.Command(plugin, handle, "idle"),
            "crouch" => manager.Command(
                plugin,
                handle,
                "crouch",
                ArgBool(args, "crouched") ?? ArgBool(args, "value") ?? true),
            _ => CommandResult.Fail("unknown-action")
        };

        return result.Ok ? AiNativeResult.Ok() : AiNativeResult.Fail(result.Reason ?? "rejected");
    }

    private static string? ArgString(IReadOnlyDictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var value) ? value as string : null;

    private static string? ArgTarget(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value))
        {
            return null;
        }

        if (value is string text)
        {
            return text;
        }

        if (value is IReadOnlyDictionary<string, object?> map &&
            map.TryGetValue("id", out var id) &&
            id is string target)
        {
            return target;
        }

        return null;
    }

    private static int? ArgInt(IReadOnlyDictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var value) && value is long number ? (int)number :
        args.TryGetValue(key, out value) && value is double real ? (int)real : null;

    private static double? ArgDouble(IReadOnlyDictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var value) && value is long number ? number :
        args.TryGetValue(key, out value) && value is double real ? real : null;

    private static bool? ArgBool(IReadOnlyDictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var value) && value is bool flag ? flag : null;

    public override void Install(JsTypeRegistry types)
    {
        _types = types;
        _inventoryType = types.Register<NpcInventoryAccessor>();
        _npcType = types.Register<BaseNPC>(arguments =>
        {
            var handle = arguments.Length > 0 ? arguments[0].ToString() : string.Empty;
            if (string.IsNullOrEmpty(handle) || WorldManager.Instance == null)
            {
                throw new InvalidOperationException("npc: instance is not bound to an entity");
            }

            var entity = WorldManager.Instance.Resolve(types.CurrentPlugin, handle)
                ?? throw new InvalidOperationException($"npc: unknown handle '{handle}'");
            var plugin = types.CurrentPlugin;
            return new BaseNPC(plugin, entity.Id, entity.Kind, entity.Name,
                result => JsPayload.FromPlain(Engine, new Dictionary<string, object?>
                {
                    ["ok"] = result.Ok,
                    ["reason"] = result.Reason
                }),
                (target, options) => InteractHandle(plugin, handle, target, options),
                target => AffordancesHandle(plugin, handle, target));
        });
    }

    [JsBinding(Scoped = true, ReturnKind = JsReturnKind.Native, Returns = "Inventory",
        Description = "Generic inventory accessor for one of this plugin's NPCs.")]
    public JsValue Inventory(string plugin, JsValue entity)
    {
        var handle = HandleOf(entity);
        if (handle == null)
        {
            throw new InvalidOperationException("npc.inventory: unknown entity");
        }

        return _inventoryType != null && _types != null
            ? _types.Instance(_inventoryType, new NpcInventoryAccessor(Engine, new NpcInventoryProvider(plugin, handle)))
            : JsValue.Null;
    }

    [JsBinding(Scoped = true, ReturnKind = JsReturnKind.Native, Returns = "Inventory",
        Description = "Generic inventory accessor for a world container.")]
    public JsValue Container(string plugin, string handle)
    {
        if (string.IsNullOrWhiteSpace(handle) || !WorldManager.TryParseHandle(handle, out _))
        {
            throw new InvalidOperationException("npc.container: a ZDO handle is required");
        }

        return _inventoryType != null && _types != null
            ? _types.Instance(_inventoryType, new NpcInventoryAccessor(Engine, new ContainerInventoryProvider(handle)))
            : JsValue.Null;
    }

    [JsBinding(Scoped = true, ReturnKind = JsReturnKind.Native, Returns = "NpcStats | null",
        Description = "Live body stats for one of this plugin's NPCs.")]
    public JsValue Stats(string plugin, JsValue entity)
    {
        var handle = HandleOf(entity);
        var obj = handle != null ? WorldManager.Instance?.Resolve(plugin, handle) : null;
        var stats = obj?.Stats;
        if (stats == null && obj?.Character != null)
        {
            stats = obj.Character.GetComponent<VjsBodyStats>();
        }

        if (stats != null)
        {
            return JsPayload.FromPlain(Engine, stats.GetView());
        }

        var perception = handle != null ? PerceptionOf(plugin, handle) : null;
        return perception?.Self != null ? JsPayload.FromPlain(Engine, perception.Self) : JsValue.Null;
    }

    [JsBinding(Scoped = true, ReturnKind = JsReturnKind.Native, Returns = "NpcAction | null",
        Description = "Current native action and its last outcome.")]
    public JsValue Action(string plugin, JsValue entity)
    {
        var handle = HandleOf(entity);
        var perception = handle != null ? PerceptionOf(plugin, handle) : null;
        return perception?.Action != null ? JsPayload.FromPlain(Engine, perception.Action.ToPlain()) : JsValue.Null;
    }

    [JsBinding(Scoped = true, ReturnKind = JsReturnKind.Native, Returns = "ActionResult",
        Description = "Requests a native pick action against a pickable ZDO.")]
    public JsValue Pick(string plugin, JsValue entity, string target)
    {
        var handle = HandleOf(entity);
        if (handle == null)
        {
            return JsPayload.FromPlain(Engine, new Dictionary<string, object?>
            {
                ["ok"] = false,
                ["reason"] = "unknown-handle"
            });
        }

        var result = WorldManager.Instance?.Command(plugin, handle, "pick", target) ??
                     CommandResult.Fail("no-world");
        return JsPayload.FromPlain(Engine, new Dictionary<string, object?>
        {
            ["ok"] = result.Ok,
            ["reason"] = result.Reason
        });
    }

    [JsBinding(Scoped = true, ReturnKind = JsReturnKind.Native, Returns = "ActionResult",
        Description = "Starts a native attack against a character in weapon range.")]
    public JsValue Attack(string plugin, JsValue entity, string target, bool secondary)
    {
        var handle = HandleOf(entity);
        var result = handle == null
            ? CommandResult.Fail("unknown-handle")
            : WorldManager.Instance?.Command(plugin, handle, "attack", target, secondary) ??
              CommandResult.Fail("no-world");
        return JsPayload.FromPlain(Engine, new Dictionary<string, object?>
        {
            ["ok"] = result.Ok,
            ["reason"] = result.Reason
        });
    }

    [JsBinding(Scoped = true, ReturnKind = JsReturnKind.Native, Returns = "ActionResult",
        Description = "Jumps once.")]
    public JsValue Jump(string plugin, JsValue entity)
    {
        var handle = HandleOf(entity);
        var result = handle == null
            ? CommandResult.Fail("unknown-handle")
            : WorldManager.Instance?.Command(plugin, handle, "jump") ?? CommandResult.Fail("no-world");
        return JsPayload.FromPlain(Engine, new Dictionary<string, object?>
        {
            ["ok"] = result.Ok,
            ["reason"] = result.Reason
        });
    }

    [JsBinding(Scoped = true, ReturnKind = JsReturnKind.Native, Returns = "ActionResult",
        Description = "Sets the native crouch/sneak state.")]
    public JsValue Crouch(string plugin, JsValue entity, bool crouched)
    {
        var handle = HandleOf(entity);
        var result = handle == null
            ? CommandResult.Fail("unknown-handle")
            : WorldManager.Instance?.Command(plugin, handle, "crouch", crouched) ??
              CommandResult.Fail("no-world");
        return JsPayload.FromPlain(Engine, new Dictionary<string, object?>
        {
            ["ok"] = result.Ok,
            ["reason"] = result.Reason
        });
    }

    [JsBinding(Scoped = true, ReturnKind = JsReturnKind.Native, Returns = "boolean",
        Description = "Reserves a world resource handle for one script actor; intention only.")]
    public bool ReserveResource(string plugin, string handle, string owner, int ttlMs)
    {
        return ResourceIntents.TryReserve(handle, owner, ttlMs);
    }

    [JsBinding(Scoped = true, ReturnKind = JsReturnKind.Native, Returns = "void",
        Description = "Releases a world resource reservation owned by one script actor.")]
    public void ReleaseResource(string plugin, string handle, string owner)
    {
        ResourceIntents.Release(handle, owner);
    }

    [JsBinding(Scoped = true, ReturnKind = JsReturnKind.Native, Returns = "CraftCheck",
        Description = "Native recipe requirements for one recipe (no plugin conditions).")]
    public JsValue CraftCheck(string plugin, JsValue entity, string recipe)
    {
        var handle = HandleOf(entity);
        var obj = handle != null ? WorldManager.Instance?.Resolve(plugin, handle) : null;
        if (obj == null)
        {
            return JsPayload.FromPlain(Engine, new Dictionary<string, object?>
            {
                ["ok"] = false,
                ["reasons"] = new List<object?> { new Dictionary<string, object?> { ["code"] = "unknown-handle" } }
            });
        }

        return JsPayload.FromPlain(Engine, NpcCrafting.Check(obj, recipe).ToPlain());
    }

    [JsBinding(Scoped = true, ReturnKind = JsReturnKind.Native, Returns = "CraftResult",
        Description = "Crafts one native recipe: consumes ingredients and adds or drops the output.")]
    public JsValue Craft(string plugin, JsValue entity, string recipe)
    {
        var handle = HandleOf(entity);
        var obj = handle != null ? WorldManager.Instance?.Resolve(plugin, handle) : null;
        if (obj == null)
        {
            return JsPayload.FromPlain(Engine, new Dictionary<string, object?>
            {
                ["ok"] = false,
                ["reason"] = "unknown-handle",
                ["count"] = 0,
                ["reasons"] = new List<object?>()
            });
        }

        if (!GameEnvironment.IsServer())
        {
            return JsPayload.FromPlain(Engine, new Dictionary<string, object?>
            {
                ["ok"] = false,
                ["reason"] = "not-authority",
                ["count"] = 0,
                ["reasons"] = new List<object?>()
            });
        }

        return JsPayload.FromPlain(Engine, NpcCrafting.Craft(obj, recipe).ToPlain());
    }

    [JsBinding(Scoped = true, ReturnKind = JsReturnKind.Native, Returns = "NpcAffordances",
        Description = "Inspects what a target object supports for one of this plugin's NPCs.")]
    public JsValue Affordances(string plugin, JsValue entity, string target)
    {
        var handle = HandleOf(entity);
        return handle == null
            ? JsPayload.FromPlain(Engine, new Dictionary<string, object?>
            {
                ["id"] = target,
                ["supported"] = false,
                ["reason"] = "unknown-handle"
            })
            : AffordancesHandle(plugin, handle, target);
    }

    [JsBinding(Scoped = true, ReturnKind = JsReturnKind.Native, Returns = "NpcInteractionResult",
        Description = "Performs one supported interaction (pick/use/store/take/addOre/addFuel).")]
    public JsValue Interact(string plugin, JsValue entity, string target, JsValue options)
    {
        var handle = HandleOf(entity);
        return InteractHandle(plugin, handle, target, options);
    }

    [JsBinding(Scoped = true, ReturnKind = JsReturnKind.Native, Returns = "unknown",
        Description = "Reads one core-owned NPC board value; null when unset.")]
    public JsValue BoardGet(string plugin, JsValue entity, string key)
    {
        var handle = HandleOf(entity);
        return handle == null ? JsValue.Null : JsPayload.FromPlain(Engine, NpcBoards.Get(handle, key));
    }

    [JsBinding(Scoped = true, ReturnKind = JsReturnKind.Native, Returns = "Record<string, unknown>",
        Description = "Snapshot of every core-owned board value for one NPC.")]
    public JsValue BoardSnapshot(string plugin, JsValue entity)
    {
        var handle = HandleOf(entity);
        return handle == null
            ? JsPayload.Object(Engine).Build()
            : JsPayload.FromPlain(Engine, NpcBoards.Snapshot(handle));
    }

    [JsBinding(Scoped = true, Description = "Subscribes to one core-owned NPC board key (empty key = all); returns an id.")]
    public double BoardOnChange(string plugin, JsValue entity, string key, JsCallback callback)
    {
        var handle = HandleOf(entity)
                     ?? throw new InvalidOperationException("npc.boardOnChange: unknown entity");
        var filter = string.IsNullOrWhiteSpace(key) ? null : key;
        var id = NpcBoards.Subscribe(plugin, handle, filter, change =>
        {
            try
            {
                callback.InvokeOn(entity, change.Key, JsPayload.FromPlain(Engine, change.NewValue));
            }
            catch (Exception exception)
            {
                LogWarn($"npc.boardOnChange listener failed: {exception.Message}");
            }
        });

        foreach (var pair in NpcBoards.Snapshot(handle))
        {
            if (filter != null && !string.Equals(pair.Key, filter, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                callback.InvokeOn(entity, pair.Key, JsPayload.FromPlain(Engine, pair.Value));
            }
            catch (Exception exception)
            {
                LogWarn($"npc.boardOnChange replay failed: {exception.Message}");
            }
        }

        return id;
    }

    [JsBinding(Scoped = true, Description = "Removes a core-owned NPC board subscription.")]
    public bool BoardOff(string plugin, double id) => NpcBoards.Unsubscribe(plugin, (long)id);

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
        var items = OptionalStrings(settings, "items");
        var sensors = Sensors(settings);
        var autoEngage = OptionalBool(settings, "autoEngage", false);
        var autoOpenDoors = OptionalBool(settings, "autoOpenDoors", true);
        if (!NpcDefinition.TryCreate(kind, prefab, tickMs, faction, items, sensors, autoEngage, autoOpenDoors,
                out var definition, out var error))
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
            Plugin = plugin,
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

        instance.PendingSpawn = true;
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

    [JsBinding(Scoped = true, ReturnKind = JsReturnKind.Native, Returns = "NpcSensed | null",
        Description = "Finds the nearest perceived target matching a declarative query.")]
    public JsValue FindObject(string plugin, JsValue entity, string kind, JsValue options)
    {
        var handle = HandleOf(entity);
        var manager = WorldManager.Instance;
        var obj = handle != null && manager != null ? manager.Resolve(plugin, handle) : null;
        if (handle == null || manager == null || obj == null)
        {
            return JsValue.Null;
        }

        var matches = manager.Find(plugin, handle, FindQuery(kind, options, 1));
        if (matches.Count == 0)
        {
            return JsValue.Null;
        }

        return JsPayload.FromPlain(Engine,
            matches[0].ToPlain(obj.Position.x, obj.Position.y, obj.Position.z, obj.Yaw, Time.realtimeSinceStartup));
    }

    [JsBinding(Scoped = true, ReturnKind = JsReturnKind.Native, Returns = "NpcSensed[]",
        Description = "Finds perceived targets matching a declarative query, nearest first.")]
    public JsValue FindObjects(string plugin, JsValue entity, string kind, JsValue options)
    {
        var handle = HandleOf(entity);
        var manager = WorldManager.Instance;
        var obj = handle != null && manager != null ? manager.Resolve(plugin, handle) : null;
        if (handle == null || manager == null || obj == null)
        {
            return JsPayload.Array(Engine, Array.Empty<JsValue?>());
        }

        var matches = manager.Find(plugin, handle, FindQuery(kind, options, int.MaxValue));
        var now = Time.realtimeSinceStartup;
        var values = new List<JsValue?>(matches.Count);
        foreach (var entry in matches)
        {
            values.Add(JsPayload.FromPlain(Engine,
                entry.ToPlain(obj.Position.x, obj.Position.y, obj.Position.z, obj.Yaw, now)));
        }

        return JsPayload.Array(Engine, values);
    }

    private static NpcFindQuery FindQuery(string kind, JsValue options, int limit)
    {
        var plain = options.IsUndefined() || options.IsNull()
            ? null
            : JsPlain.From(options) as IReadOnlyDictionary<string, object?>;
        var query = NpcFindQuery.FromPlain(plain, string.IsNullOrWhiteSpace(kind) ? null : kind);
        query.Max = Math.Max(1, limit);
        return query;
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
        if (WorldManager.Instance is { } manager && _blueprints.TryGetValue(plugin, out var kinds))
        {
            foreach (var blueprint in kinds.Values)
            {
                manager.UnregisterKind(plugin, blueprint.Kind);
            }
        }

        _blueprints.Remove(plugin);
        _debug.Remove(plugin);
        NpcBoards.Release(plugin);
        AiSenses.Clear(plugin);
    }

    public override void Dispose()
    {
        if (WorldManager.Instance is { } manager)
        {
            foreach (var blueprint in _blueprints.Values.SelectMany(kinds => kinds.Values))
            {
                manager.UnregisterKind(blueprint.Plugin, blueprint.Kind);
            }
        }

        _blueprints.Clear();
        _debug.Clear();
        NpcBoards.Clear();
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
                LogInfo($"[{blueprint.Plugin}] [npc] {blueprint.Kind} discovered {handle}");
            }

            // Clients cannot observe the spawn: any handle they discover already existed, so the
            // first hydration is a rehydrate. Only Spawn() marks an instance as newly spawned.
            if (!instance.Hydrated && !GameEnvironment.IsServer() &&
                BodyReady(blueprint.Plugin, handle))
            {
                var spawned = instance.PendingSpawn;
                instance.PendingSpawn = false;
                instance.Hydrated = true;
                Invoke(blueprint, instance, spawned ? instance.OnSpawn : instance.OnRehydrate,
                    spawned ? "onSpawn" : "onRehydrate", View(blueprint.Plugin, handle));
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

    private static bool BodyReady(string plugin, string handle)
    {
        var obj = WorldManager.Instance?.Resolve(plugin, handle);
        if (obj != null && obj.BodyReady)
        {
            return true;
        }

        var instance = ZNetScene.instance?.FindInstance(ResolveId(handle));
        var stats = instance?.GetComponent<VjsBodyStats>();
        return stats != null && stats.Ready;
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
            var obj = manager.Resolve(blueprint.Plugin, instance.Handle);
            if (obj == null || !obj.BodyReady)
            {
                continue;
            }

            var perception = PerceptionOf(blueprint.Plugin, instance.Handle);
            if (perception == null)
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

            foreach (var entry in manager.TakeHeard(blueprint.Plugin, instance.Handle))
            {
                if (_debug.Contains(blueprint.Plugin))
                {
                    LogInfo(
                        $"[{blueprint.Plugin}] [npc] {blueprint.Kind} {instance.Handle} heard " +
                        $"{entry.Name} noise={entry.Noise:0.0} distance={entry.Distance:0.0}");
                }

                Invoke(blueprint, instance, instance.OnHeard, "onHeard",
                    JsPayload.FromPlain(Engine, entry.ToPlain(obj.Position.x, obj.Position.y, obj.Position.z, obj.Yaw, now)));
            }

            foreach (var damage in manager.TakeDamage(blueprint.Plugin, instance.Handle))
            {
                if (_debug.Contains(blueprint.Plugin))
                {
                    LogInfo(
                        $"[{blueprint.Plugin}] [npc] {blueprint.Kind} {instance.Handle} damaged " +
                        $"{damage.Damage:0.0} by {damage.AttackerName} ({damage.AttackerId})");
                }

                Invoke(blueprint, instance, instance.OnDamaged, "onDamaged",
                    JsPayload.FromPlain(Engine, damage.ToPlain()));
            }

            ReportSettlements(blueprint, instance, perception, obj.Yaw);

            var view = Payload(perception, obj.Yaw);
            if (!instance.Hydrated)
            {
                instance.Hydrated = true;
                instance.Failures.Clear();
                instance.DisabledHooks.Clear();
                var spawned = instance.PendingSpawn;
                instance.PendingSpawn = false;
                if (_debug.Contains(blueprint.Plugin))
                {
                    LogInfo($"[{blueprint.Plugin}] [npc] {blueprint.Kind} {instance.Handle} {(spawned ? "spawned" : "rehydrated")}");
                }
                Invoke(blueprint, instance, spawned ? instance.OnSpawn : instance.OnRehydrate,
                    spawned ? "onSpawn" : "onRehydrate", view);
            }

            Invoke(blueprint, instance, instance.OnTick, "onTick", view);
        }
    }

    private void ReportSettlements(Blueprint blueprint, Instance instance, NpcPerception perception, float yaw)
    {
        foreach (var action in WorldManager.Instance?.TakeActionSettlements(blueprint.Plugin, instance.Handle) ??
                                   Array.Empty<NpcActionView>())
        {
            if (_debug.Contains(blueprint.Plugin))
            {
                var target = action.X.HasValue
                    ? $"{action.X.Value:0.0},{action.Y ?? 0d:0.0},{action.Z ?? 0d:0.0}"
                    : "-";
                LogInfo(
                    $"[{blueprint.Plugin}] [npc] {blueprint.Kind} {instance.Handle} action " +
                    $"#{action.Seq} {action.Kind} {action.Status} reason={action.Reason ?? "-"} target={target} " +
                    $"id={action.TargetId ?? "-"}");
            }

            Invoke(blueprint, instance, instance.OnAction, "onAction",
                Payload(perception, yaw), JsPayload.FromPlain(Engine, action.ToPlain()));
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
                OnHeard = Hook(target, "onHeard", blueprint.Kind),
                OnDamaged = Hook(target, "onDamaged", blueprint.Kind),
                OnAction = Hook(target, "onAction", blueprint.Kind)
            };
        }
        catch (Exception exception)
        {
            LogError($"[{blueprint.Plugin}] npc '{handle}' construction failed: {JsErrorText.Describe(exception)}");
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
        if (callback == null || instance.DisabledHooks.Contains(hook))
        {
            return;
        }

        try
        {
            callback.InvokeOn(instance.Object, arguments);
            instance.Failures.Remove(hook);
        }
        catch (Exception exception)
        {
            instance.Failures.TryGetValue(hook, out var failures);
            failures++;
            instance.Failures[hook] = failures;
            LogError($"[{blueprint.Plugin}] npc '{instance.Handle}' {hook} threw ({failures}): {JsErrorText.Describe(exception)}");
            if (failures >= 3)
            {
                instance.DisabledHooks.Add(hook);
                LogError($"[{blueprint.Plugin}] npc '{instance.Handle}' {hook} disabled after repeated failures");
            }

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
            Hearing = (float)Number(perception, "hearing", defaults.Hearing),
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
        query.AudibleOnly = Bool(obj, "audible", query.AudibleOnly);
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

    private static IReadOnlyList<string>? OptionalStrings(ObjectInstance? options, string name)
    {
        var value = options?.Get(name) ?? JsValue.Undefined;
        if (value.IsUndefined() || value.IsNull())
        {
            return null;
        }

        if (value is not JsArray array)
        {
            throw new ArgumentException($"npc: '{name}' must be an array of strings");
        }

        var list = new List<string>();
        for (var index = 0u; index < array.Length; index++)
        {
            var item = array.Get(index);
            if (!item.IsString())
            {
                throw new ArgumentException($"npc: '{name}' must be an array of strings");
            }

            list.Add(item.AsString());
        }

        return list;
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

    private static bool OptionalBool(ObjectInstance? options, string name, bool fallback)
    {
        var value = options?.Get(name) ?? JsValue.Undefined;
        if (value.IsUndefined() || value.IsNull())
        {
            return fallback;
        }

        return value.IsBoolean() ? value.AsBoolean() : throw new ArgumentException($"npc: '{name}' must be a boolean");
    }

    private JsValue AffordancesHandle(string plugin, string? handle, string target)
    {
        var obj = handle != null ? WorldManager.Instance?.Resolve(plugin, handle) : null;
        return obj == null
            ? JsPayload.FromPlain(Engine, new Dictionary<string, object?>
            {
                ["id"] = target,
                ["supported"] = false,
                ["reason"] = "unknown-handle"
            })
            : JsPayload.FromPlain(Engine, NpcInteraction.Inspect(obj, target).ToPlain());
    }

    private JsValue InteractHandle(string plugin, string? handle, string target, JsValue options)
    {
        var obj = handle != null ? WorldManager.Instance?.Resolve(plugin, handle) : null;
        var result = obj == null
            ? NpcInteractionResult.Fail("unknown-handle")
            : NpcInteraction.Interact(obj, target, InteractOptions(options));
        return JsPayload.FromPlain(Engine, result.ToPlain());
    }

    private static NpcInteractOptions InteractOptions(JsValue value)
    {
        var options = new NpcInteractOptions();
        if (value is not ObjectInstance obj)
        {
            return options;
        }

        options.Verb = OptionalString(obj, "verb");
        options.Prefab = OptionalString(obj, "prefab");
        options.Name = OptionalString(obj, "name");
        if (obj.Get("slot") is ObjectInstance slot)
        {
            var x = OptionalNumber(slot, "x");
            var y = OptionalNumber(slot, "y");
            if (x.HasValue) options.X = (int)Math.Round(x.Value);
            if (y.HasValue) options.Y = (int)Math.Round(y.Value);
        }

        options.Count = Math.Max(1, (int)Math.Round(Number(obj, "count", 1d)));
        options.Repeat = Bool(obj, "repeat", false);
        options.Alt = Bool(obj, "alt", false);
        return options;
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

}
