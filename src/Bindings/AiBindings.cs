using System;
using System.Collections.Generic;
using System.Linq;
using Jint;
using Jint.Native;
using Jint.Native.Function;
using Jint.Native.Object;
using Jint.Runtime;
using ValheimJS.Core;
using ValheimJS.Scripting.Binding;

namespace ValheimJS.Bindings;

/// <summary>
/// YAML behaviour trees for scripts. Trees are compiled once; every instance keeps independent
/// state and forwards actions/conditions/scripts to the JS callbacks supplied at creation time.
/// <para>
/// The engine ticks only when the script calls <c>tick</c>, so NPC AI stays on the NPC host cadence
/// (<c>onTick</c>) while pure logic can run anywhere. When <c>this</c> has a string <c>id</c>, the
/// NPC's perception store is injected as <c>ctx.state.senses</c> before each tick. NPCs are driven
/// through the existing <c>npc</c> surface: an action calls <c>moveTo</c>/<c>idle</c> and returns
/// <c>running</c> until the intent settles.
/// </para>
/// </summary>
[JsBinder("ai", Phase = JsBindPhase.Gameplay, Order = 145,
    Description = "YAML behaviour trees: compile, run per-entity instances, and bridge JS actions/conditions.")]
internal sealed class AiBindings : ScopedBindings<string, AiBindings.Brain>
{
    private const int MaxTreesPerPlugin = 32;
    private const int MaxInstancesPerPlugin = 64;
    private const int MaxImports = 32;
    private const int MaxYamlCharacters = 512 * 1024;

    internal sealed class Brain
    {
        internal string Id = string.Empty;
        internal string TreeId = string.Empty;
        internal BehaviourTree Tree = null!;
        internal BehaviourInstance Instance = null!;
        internal JsValue This = JsValue.Undefined;
        internal JsCallback? Exec;
        internal JsCallback? Action;
        internal JsCallback? Condition;
        internal JsCallback? Random;
        internal string? SensesKey;
        internal bool Senses = true;
    }

    private sealed class TreeEntry
    {
        internal string Id = string.Empty;
        internal BehaviourTree Tree = null!;
    }

    private readonly Dictionary<string, Dictionary<string, TreeEntry>> _trees = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _treeIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _brainIds = new(StringComparer.Ordinal);

    public AiBindings(JsBindingContext context) : base(context)
    {
    }

    [JsBinding(Scoped = true, Description = "Compiles YAML into a tree id. Imports come from options.imports keyed by alias.")]
    public string Compile(string plugin, string yaml, JsValue options)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            throw new ArgumentException("ai.compile: YAML is required.");
        }

        if (yaml.Length > MaxYamlCharacters)
        {
            throw new ArgumentException($"ai.compile: YAML exceeds {MaxYamlCharacters} characters.");
        }

        var trees = Trees(plugin);
        if (trees.Count >= MaxTreesPerPlugin)
        {
            throw new InvalidOperationException($"ai.compile: this plugin already has {MaxTreesPerPlugin} trees.");
        }

        var settings = Object(options, "ai.compile(options)");
        var source = OptionalString(settings, "source", "ai.compile(options.source)") ?? "<script>";
        var imports = new Dictionary<string, string>(StringComparer.Ordinal);
        if (settings != null && settings.Get("imports") is ObjectInstance importObject)
        {
            foreach (var key in importObject.GetOwnPropertyKeys(Types.String))
            {
                if (imports.Count >= MaxImports)
                {
                    throw new ArgumentException($"ai.compile: imports exceed {MaxImports} entries.");
                }

                var alias = key.ToString();
                var value = importObject.Get(alias);
                if (!value.IsString())
                {
                    throw new ArgumentException($"ai.compile: import '{alias}' must be YAML text.");
                }

                imports[alias] = value.AsString();
            }
        }

        var warn = settings?.Get("warn") is Function warnFunction
            ? new JsCallback(Engine, warnFunction, "ai.compile(warn)")
            : null;
        Action<string>? report = warn != null ? message => warn.Invoke(message) : null;

        var tree = BehaviourTree.Parse(
            yaml,
            source,
            report,
            alias => imports.TryGetValue(alias, out var text) ? text : null);

        var id = "tree" + Next(_treeIds, plugin);
        trees.Add(id, new TreeEntry { Id = id, Tree = tree });
        return id;
    }

    [JsBinding(Scoped = true, Description = "Creates an independent instance of a compiled tree; returns its id.")]
    public string Instantiate(string plugin, string treeId, JsValue options)
    {
        var tree = RequireTree(plugin, treeId);
        var brains = For(plugin);
        if (brains.Count >= MaxInstancesPerPlugin)
        {
            throw new InvalidOperationException($"ai.instantiate: this plugin already has {MaxInstancesPerPlugin} instances.");
        }

        var settings = Object(options, "ai.instantiate(options)");
        var brain = new Brain
        {
            TreeId = treeId,
            Tree = tree,
            This = settings?.Get("this") ?? JsValue.Undefined,
            Exec = Callback(settings, "exec", "ai.instantiate(exec)"),
            Action = Callback(settings, "action", "ai.instantiate(action)"),
            Condition = Callback(settings, "condition", "ai.instantiate(condition)"),
            Random = Callback(settings, "random", "ai.instantiate(random)")
        };

        var senses = settings?.Get("senses") ?? JsValue.Undefined;
        if (!senses.IsUndefined() && !senses.IsNull())
        {
            if (!senses.IsBoolean())
            {
                throw new ArgumentException("ai.instantiate: senses must be a boolean.");
            }

            brain.Senses = senses.AsBoolean();
        }

        brain.SensesKey = ReadId(brain.This);

        brain.Instance = tree.CreateInstance(
            host: brain,
            execute: (code, context) =>
            {
                InvokeScript(brain, code, context);
                return null;
            },
            action: (name, context) => InvokeAction(brain, name, context),
            condition: (name, context) => InvokeCondition(brain, name, context),
            random: brain.Random != null ? () => InvokeRandom(brain) : null);

        brain.Id = "brain" + Next(_brainIds, plugin);
        brains.Add(brain.Id, brain);

        if (settings != null && settings.Get("state") is ObjectInstance state)
        {
            if (FromJs(state) is not Dictionary<string, object?> overrides)
            {
                throw new ArgumentException("ai.instantiate: state must be an object.");
            }

            foreach (var pair in overrides)
            {
                brain.Instance.State[pair.Key] = pair.Value;
            }
        }

        var limit = settings?.Get("maxJumpsPerTick") ?? JsValue.Undefined;
        if (!limit.IsUndefined() && !limit.IsNull())
        {
            if (!limit.IsNumber())
            {
                throw new ArgumentException("ai.instantiate: maxJumpsPerTick must be a number.");
            }

            var numeric = limit.AsNumber();
            if (double.IsNaN(numeric) || double.IsInfinity(numeric) || numeric < 1 || numeric > 10000)
            {
                throw new ArgumentException("ai.instantiate: maxJumpsPerTick must be between 1 and 10000.");
            }

            brain.Instance.MaxJumpsPerTick = (int)numeric;
        }

        return brain.Id;
    }

    [JsBinding(Scoped = true, ReturnKind = JsReturnKind.Native, Returns = "AiTickResult",
        Description = "Advances one instance; status is success, failure, running or cancelled.")]
    public JsValue Tick(string plugin, string instanceId, double deltaTime)
    {
        var brain = Require(plugin, instanceId, "ai: instance");
        if (brain.Senses && brain.SensesKey != null)
        {
            var senses = AiSenses.Get(plugin, brain.SensesKey);
            if (senses != null)
            {
                brain.Instance.State["senses"] = BehaviourTree.Clone(senses);
            }
        }

        var result = brain.Instance.Tick((float)deltaTime);
        return JsPayload.Object(Engine)
            .Set("status", StatusName(result.Status))
            .Set("output", ToJs(result.Output))
            .Build();
    }

    [JsBinding(Scoped = true, ReturnKind = JsReturnKind.Native, Returns = "Record<string, unknown>",
        Description = "Deep copy of the instance state.")]
    public JsValue State(string plugin, string instanceId)
    {
        var brain = Require(plugin, instanceId, "ai: instance");
        return ToJsMap(brain.Instance.State);
    }

    [JsBinding(Scoped = true, Description = "Replaces one top-level state value.")]
    public void Set(string plugin, string instanceId, string key, JsValue value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("ai.set: a state key is required.");
        }

        Require(plugin, instanceId, "ai: instance").Instance.State[key] = FromJs(value);
    }

    [JsBinding(Scoped = true, Description = "Current status of the last tick, or cancelled.")]
    public string Status(string plugin, string instanceId) =>
        StatusName(Require(plugin, instanceId, "ai: instance").Instance.Status);

    [JsBinding(Scoped = true, Description = "Stops the instance and runs active execAfter hooks as interrupted.")]
    public void Cancel(string plugin, string instanceId) =>
        Require(plugin, instanceId, "ai: instance").Instance.Cancel();

    [JsBinding(Scoped = true, Description = "Clears execution progress; optionally restores declared state defaults.")]
    public void Reset(string plugin, string instanceId, bool resetState) =>
        Require(plugin, instanceId, "ai: instance").Instance.Reset(resetState);

    [JsBinding(Scoped = true, Description = "Drops one instance without running hooks.")]
    public bool Release(string plugin, string instanceId) => Remove(plugin, instanceId, out _);

    [JsBinding(Scoped = true, Description = "Drops a compiled tree and every instance using it.")]
    public bool Unload(string plugin, string treeId)
    {
        var trees = Trees(plugin);
        if (!trees.Remove(treeId))
        {
            return false;
        }

        var brains = For(plugin);
        foreach (var brain in brains.Values.Where(candidate => candidate.TreeId == treeId).ToList())
        {
            brains.Remove(brain.Id);
        }

        return true;
    }

    public override void Release(string plugin)
    {
        base.Release(plugin);
        _trees.Remove(plugin);
        _treeIds.Remove(plugin);
        _brainIds.Remove(plugin);
        AiSenses.Clear(plugin);
    }

    public override void Dispose()
    {
        _trees.Clear();
        _treeIds.Clear();
        _brainIds.Clear();
        AiSenses.Clear();
        base.Dispose();
    }

    private void InvokeScript(Brain brain, string code, BehaviourContext context)
    {
        if (brain.Exec == null)
        {
            throw new BehaviourException("ai: the tree uses inline scripts but the instance has no exec callback.");
        }

        var payload = ContextPayload(brain, context);
        brain.Exec.InvokeOn(brain.This, code, payload);
        SyncState(context, payload);
    }

    private BehaviourResult InvokeAction(Brain brain, string name, BehaviourContext context)
    {
        if (brain.Action == null)
        {
            throw new BehaviourException($"ai: the tree uses action '{name}' but the instance has no action callback.");
        }

        var payload = ContextPayload(brain, context);
        var result = brain.Action.InvokeOn(brain.This, name, payload);
        SyncState(context, payload);
        return ParseResult(result, name);
    }

    private bool InvokeCondition(Brain brain, string name, BehaviourContext context)
    {
        if (brain.Condition == null)
        {
            throw new BehaviourException($"ai: the tree uses condition '{name}' but the instance has no condition callback.");
        }

        var payload = ContextPayload(brain, context);
        var result = brain.Condition.InvokeOn(brain.This, name, payload);
        SyncState(context, payload);
        if (!result.IsBoolean())
        {
            throw new BehaviourException($"ai: condition '{name}' must return a boolean.");
        }

        return result.AsBoolean();
    }

    private double InvokeRandom(Brain brain)
    {
        var result = brain.Random!.Invoke();
        if (!result.IsNumber())
        {
            throw new BehaviourException("ai: random must return a number.");
        }

        var value = result.AsNumber();
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new BehaviourException("ai: random must return a finite number.");
        }

        return value;
    }

    private static BehaviourResult ParseResult(JsValue result, string name)
    {
        if (result.IsNull() || result.IsUndefined())
        {
            return BehaviourResult.Success();
        }

        if (result.IsString())
        {
            return new BehaviourResult(ParseStatus(result.AsString(), name));
        }

        if (result is ObjectInstance obj)
        {
            var status = obj.Get("status");
            var parsed = status.IsUndefined() || status.IsNull()
                ? BehaviourStatus.Success
                : ParseStatus(status.IsString() ? status.AsString() : throw new BehaviourException(
                    $"ai: action '{name}' status must be a string."), name);
            var output = obj.Get("output");
            return new BehaviourResult(parsed, output.IsUndefined() ? null : FromJs(output));
        }

        throw new BehaviourException(
            $"ai: action '{name}' must return \"success\", \"failure\", \"running\" or {{ status, output }}.");
    }

    private static BehaviourStatus ParseStatus(string status, string name) => status switch
    {
        "success" => BehaviourStatus.Success,
        "failure" => BehaviourStatus.Failure,
        "running" => BehaviourStatus.Running,
        _ => throw new BehaviourException($"ai: action '{name}' returned unknown status '{status}'.")
    };

    private JsValue ContextPayload(Brain brain, BehaviourContext context)
    {
        var args = JsPayload.Object(Engine);
        foreach (var pair in context.Args)
        {
            args.Set(pair.Key, ToJs(pair.Value));
        }

        return JsPayload.Object(Engine)
            .Set("args", args.Build())
            .Set("state", ToJsMap(context.State))
            .Set("this", brain.This)
            .Set("deltaTime", (double)context.DeltaTime)
            .Set("interrupted", context.Interrupted)
            .Build();
    }

    private static void SyncState(BehaviourContext context, JsValue payload)
    {
        if (payload is not ObjectInstance obj)
        {
            return;
        }

        var state = obj.Get("state");
        if (state.IsUndefined() || state.IsNull())
        {
            return;
        }

        if (state is not ObjectInstance)
        {
            throw new BehaviourException("ai: ctx.state must stay an object.");
        }

        if (FromJs(state) is not Dictionary<string, object?> snapshot)
        {
            throw new BehaviourException("ai: ctx.state could not be read back.");
        }

        context.State.Clear();
        foreach (var pair in snapshot)
        {
            context.State[pair.Key] = pair.Value;
        }
    }

    private JsValue ToJsMap(IEnumerable<KeyValuePair<string, object?>> entries)
    {
        var builder = JsPayload.Object(Engine);
        foreach (var pair in entries)
        {
            builder.Set(pair.Key, ToJs(pair.Value));
        }

        return builder.Build();
    }

    private JsValue ToJs(object? value)
    {
        switch (value)
        {
            case null:
                return JsValue.Null;
            case JsValue js:
                return js;
            case string text:
                return JsValue.FromObject(Engine, text);
            case bool flag:
                return JsValue.FromObject(Engine, flag);
            case double number:
                return JsValue.FromObject(Engine, number);
            case float single:
                return JsValue.FromObject(Engine, (double)single);
            case long integer:
                return JsValue.FromObject(Engine, (double)integer);
            case int integer:
                return JsValue.FromObject(Engine, (double)integer);
            case IReadOnlyDictionary<string, object?> map:
                return ToJsMap(map);
            case IDictionary<string, object?> map:
                return ToJsMap(map);
            case List<object?> list:
                return JsPayload.Array(Engine, list.Select(ToJs));
            case object?[] array:
                return JsPayload.Array(Engine, array.Select(ToJs));
            default:
                throw new BehaviourException($"ai: value of type '{value.GetType().Name}' cannot cross into JS.");
        }
    }

    private static object? FromJs(JsValue value)
    {
        if (value.IsNull() || value.IsUndefined())
        {
            return null;
        }

        if (value.IsString())
        {
            return value.AsString();
        }

        if (value.IsBoolean())
        {
            return value.AsBoolean();
        }

        if (value.IsNumber())
        {
            return value.AsNumber();
        }

        if (value.IsArray())
        {
            var array = value.AsArray();
            var length = (int)array.Length;
            var list = new List<object?>(length);
            for (var index = 0; index < length; index++)
            {
                list.Add(FromJs(array.Get(index)));
            }

            return list;
        }

        if (value is ObjectInstance obj)
        {
            var map = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var key in obj.GetOwnPropertyKeys(Types.String))
            {
                var name = key.ToString();
                map[name] = FromJs(obj.Get(name));
            }

            return map;
        }

        throw new BehaviourException($"ai: a JS value of type '{value.Type}' cannot be stored in state.");
    }

    private JsCallback? Callback(ObjectInstance? options, string name, string label)
    {
        var value = options?.Get(name) ?? JsValue.Undefined;
        if (value.IsUndefined() || value.IsNull())
        {
            return null;
        }

        if (value is not Function function)
        {
            throw new ArgumentException($"{label} must be a function.");
        }

        return new JsCallback(Engine, function, label);
    }

    private static string? ReadId(JsValue value)
    {
        if (value is not ObjectInstance obj)
        {
            return null;
        }

        var id = obj.Get("id");
        return id.IsString() ? id.AsString() : null;
    }

    private static ObjectInstance? Object(JsValue value, string label)
    {
        if (value.IsUndefined() || value.IsNull())
        {
            return null;
        }

        return value as ObjectInstance ?? throw new ArgumentException($"{label} must be an object.");
    }

    private static string? OptionalString(ObjectInstance? options, string name, string label)
    {
        var value = options?.Get(name) ?? JsValue.Undefined;
        if (value.IsUndefined() || value.IsNull())
        {
            return null;
        }

        return value.IsString() ? value.AsString() : throw new ArgumentException($"{label} must be a string.");
    }

    private BehaviourTree RequireTree(string plugin, string treeId)
    {
        if (Trees(plugin).TryGetValue(treeId, out var entry))
        {
            return entry.Tree;
        }

        throw new InvalidOperationException($"ai: tree '{treeId}' does not exist for this plugin.");
    }

    private Dictionary<string, TreeEntry> Trees(string plugin)
    {
        if (!_trees.TryGetValue(plugin, out var trees))
        {
            trees = new Dictionary<string, TreeEntry>(StringComparer.Ordinal);
            _trees[plugin] = trees;
        }

        return trees;
    }

    private static int Next(Dictionary<string, int> counters, string plugin)
    {
        counters.TryGetValue(plugin, out var value);
        value++;
        counters[plugin] = value;
        return value;
    }

    private static string StatusName(BehaviourStatus status) => status switch
    {
        BehaviourStatus.Success => "success",
        BehaviourStatus.Failure => "failure",
        BehaviourStatus.Running => "running",
        _ => "cancelled"
    };
}
