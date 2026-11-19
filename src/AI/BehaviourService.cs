using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Jint;
using Jint.Native;
using Jint.Native.Function;
using Jint.Native.Object;
using Jint.Runtime;
using ValheimJS.Scripting.Binding;

namespace ValheimJS.AI;

/// <summary>
/// Per-engine behaviour tree runtime shared by the <c>ai</c> and <c>planning</c> bindings.
/// Compiled definitions and instances live here; bindings are thin adapters.
/// </summary>
internal sealed class BehaviourService : IDisposable
{
    private const int MaxTreesPerPlugin = 32;
    private const int MaxInstancesPerPlugin = 64;
    private const int MaxImports = 32;
    private const int MaxYamlCharacters = 512 * 1024;

    internal sealed class Brain
    {
        internal string Id = string.Empty;
        internal string Plugin = string.Empty;
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
        internal readonly Dictionary<string, object?> NativeState = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, ActionTrace> Actions = new(StringComparer.Ordinal);
    }

    internal sealed class ActionTrace
    {
        internal string Name = string.Empty;
        internal long Started;
    }

    private sealed class TreeEntry
    {
        internal string Id = string.Empty;
        internal BehaviourTree Tree = null!;
    }

    private readonly JsBindingContext _context;
    private readonly Dictionary<string, Dictionary<string, TreeEntry>> _trees = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _treeIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _brainIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _debug = new(StringComparer.Ordinal);

    internal BehaviourService(JsBindingContext context)
    {
        _context = context;
    }

    private Engine Engine => _context.Engine;

    internal bool Debug(string plugin, bool enabled)
    {
        if (enabled) _debug.Add(plugin);
        else _debug.Remove(plugin);
        foreach (var brain in For(plugin).Values)
        {
            ApplyTrace(plugin, brain);
        }
        return enabled;
    }

    internal string Compile(string plugin, string yaml, JsValue options)
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
            foreach (var key in importObject.GetOwnPropertyKeys(Jint.Runtime.Types.String))
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

    internal string Instantiate(string plugin, string treeId, JsValue options)
    {
        var tree = RequireTree(plugin, treeId);
        var brains = For(plugin);
        if (brains.Count >= MaxInstancesPerPlugin)
        {
            throw new InvalidOperationException($"ai.instantiate: this plugin already has {MaxInstancesPerPlugin} instances.");
        }

        var settings = Object(options, "ai.instantiate(options)");
        var brain = CreateBrain(plugin, treeId, tree);
        brain.This = settings?.Get("this") ?? JsValue.Undefined;
        brain.Exec = Callback(settings, "exec", "ai.instantiate(exec)");
        brain.Action = Callback(settings, "action", "ai.instantiate(action)");
        brain.Condition = Callback(settings, "condition", "ai.instantiate(condition)");
        brain.Random = Callback(settings, "random", "ai.instantiate(random)");

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
        ApplyTrace(plugin, brain);

        if (settings != null && settings.Get("state") is ObjectInstance state)
        {
            if (JsPlain.From(state) is not Dictionary<string, object?> overrides)
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

    /// <summary>Creates an instance from host callbacks for a planning/behaviour goal.</summary>
    internal string InstantiateForGoal(
        string plugin,
        string treeId,
        JsValue host,
        JsCallback? action,
        JsCallback? condition,
        JsCallback? exec,
        JsCallback? random,
        JsValue state,
        int? maxJumpsPerTick,
        bool senses = true)
    {
        var tree = RequireTree(plugin, treeId);
        var brains = For(plugin);
        if (brains.Count >= MaxInstancesPerPlugin)
        {
            throw new InvalidOperationException($"ai.instantiate: this plugin already has {MaxInstancesPerPlugin} instances.");
        }

        var brain = CreateBrain(plugin, treeId, tree);
        brain.This = host;
        brain.Action = action;
        brain.Condition = condition;
        brain.Exec = exec;
        brain.Random = random;
        brain.Senses = senses;
        brain.SensesKey = ReadId(host);
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
        ApplyTrace(plugin, brain);

        if (!state.IsUndefined() && !state.IsNull())
        {
            if (JsPlain.From(state) is not Dictionary<string, object?> overrides)
            {
                throw new ArgumentException("behaviour goal state must be an object.");
            }

            foreach (var pair in overrides)
            {
                brain.Instance.State[pair.Key] = pair.Value;
            }
        }

        if (maxJumpsPerTick is { } jumps)
        {
            brain.Instance.MaxJumpsPerTick = Math.Clamp(jumps, 1, 10000);
        }

        return brain.Id;
    }

    internal JsValue Tick(string plugin, string instanceId, double deltaTime)
    {
        var result = TickStatus(plugin, instanceId, (float)deltaTime, out var output);
        return JsPayload.Object(Engine)
            .Set("status", StatusName(result))
            .Set("output", JsPlain.ToJs(Engine, output))
            .Build();
    }

    internal BehaviourStatus TickStatus(
        string plugin,
        string instanceId,
        float deltaTime,
        out object? output)
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

        var result = brain.Instance.Tick(deltaTime);
        output = result.Output;
        return result.Status;
    }

    internal JsValue State(string plugin, string instanceId)
    {
        var brain = Require(plugin, instanceId, "ai: instance");
        return ToJsMap(brain.Instance.State);
    }

    internal void Set(string plugin, string instanceId, string key, JsValue value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("ai.set: a state key is required.");
        }

        Require(plugin, instanceId, "ai: instance").Instance.State[key] = JsPlain.From(value);
    }

    internal string Status(string plugin, string instanceId) =>
        StatusName(Require(plugin, instanceId, "ai: instance").Instance.Status);

    internal void Cancel(string plugin, string instanceId)
    {
        var brain = Require(plugin, instanceId, "ai: instance");
        brain.NativeState.Clear();
        brain.Instance.Cancel();
    }

    internal void Reset(string plugin, string instanceId, bool resetState)
    {
        var brain = Require(plugin, instanceId, "ai: instance");
        brain.NativeState.Clear();
        brain.Instance.Reset(resetState);
    }

    internal bool Release(string plugin, string instanceId) => Remove(plugin, instanceId, out _);

    internal bool Unload(string plugin, string treeId)
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

    internal void ReleasePlugin(string plugin)
    {
        _trees.Remove(plugin);
        _treeIds.Remove(plugin);
        _brainIds.Remove(plugin);
        _debug.Remove(plugin);
        _brainsByPlugin.Remove(plugin);
        AiSenses.Clear(plugin);
    }

    public void Dispose()
    {
        _trees.Clear();
        _treeIds.Clear();
        _brainIds.Clear();
        _brainsByPlugin.Clear();
        AiSenses.Clear();
    }

    private BehaviourService.Brain CreateBrain(string plugin, string treeId, BehaviourTree tree) =>
        new()
        {
            Plugin = plugin,
            TreeId = treeId,
            Tree = tree
        };

    private void ApplyTrace(string plugin, Brain brain) =>
        brain.Instance.Trace = _debug.Contains(plugin)
            ? line => _context.Log("info", $"[{plugin}] [ai] {line}")
            : null;

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
        var debug = _debug.Contains(brain.Plugin);
        var traceKey = context.NodeId;
        if (debug && !brain.Actions.TryGetValue(traceKey, out _))
        {
            brain.Actions[traceKey] = new ActionTrace { Name = name, Started = NowMs() };
            _context.Log(
                "info",
                $"[{brain.Plugin}] [ai] actor={brain.SensesKey ?? "-"} action start {name} " +
                $"args={DescribeArgs(context.Args)}");
        }

        BehaviourResult result;
        if (brain.SensesKey != null && AiNative.Resolve is { } native)
        {
            var nativeResult = native(
                brain.Plugin,
                brain.SensesKey,
                context.NodeId,
                name,
                context.Args,
                brain.NativeState);
            if (nativeResult.Handled)
            {
                if (!debug || nativeResult.Running)
                {
                    if (nativeResult.Running)
                    {
                        return BehaviourResult.Running;
                    }
                }
                else
                {
                    _context.Log(
                        "info",
                        $"[{brain.Plugin}] [ai] actor={brain.SensesKey} native {name} " +
                        $"success={nativeResult.Success} paused={nativeResult.Paused} output={DescribeValue(nativeResult.Output)}");
                }

                result = nativeResult.Success
                    ? BehaviourResult.Success(nativeResult.Output)
                    : BehaviourResult.Failure(nativeResult.Output);
            }
            else
            {
                result = InvokeScriptAction(brain, name, context);
            }
        }
        else
        {
            result = InvokeScriptAction(brain, name, context);
        }

        if (debug && result.Status != BehaviourStatus.Running && brain.Actions.Remove(traceKey, out var trace))
        {
            _context.Log(
                "info",
                $"[{brain.Plugin}] [ai] actor={brain.SensesKey ?? "-"} action end {trace.Name} " +
                $"status={result.Status} ms={NowMs() - trace.Started} output={DescribeValue(result.Output)}");
        }

        return result;
    }

    private BehaviourResult InvokeScriptAction(Brain brain, string name, BehaviourContext context)
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

    private static long NowMs() => (long)(Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency);

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
            return new BehaviourResult(parsed, output.IsUndefined() ? null : JsPlain.From(output));
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
            args.Set(pair.Key, JsPlain.ToJs(Engine, pair.Value));
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

        if (JsPlain.From(state) is not Dictionary<string, object?> snapshot)
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
            builder.Set(pair.Key, JsPlain.ToJs(Engine, pair.Value));
        }

        return builder.Build();
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

    private Dictionary<string, Brain> For(string plugin)
    {
        if (!_brainsByPlugin.TryGetValue(plugin, out var brains))
        {
            brains = new Dictionary<string, Brain>(StringComparer.Ordinal);
            _brainsByPlugin[plugin] = brains;
        }

        return brains;
    }

    private readonly Dictionary<string, Dictionary<string, Brain>> _brainsByPlugin = new(StringComparer.Ordinal);

    private static int Next(Dictionary<string, int> counters, string plugin)
    {
        counters.TryGetValue(plugin, out var value);
        value++;
        counters[plugin] = value;
        return value;
    }

    private static string DescribeArgs(IReadOnlyDictionary<string, object?> args) =>
        args.Count == 0
            ? "{}"
            : "{" + string.Join(", ", args.Select(pair => $"{pair.Key}={DescribeValue(pair.Value)}")) + "}";

    private static string DescribeValue(object? value) => value switch
    {
        null => "null",
        string text => text,
        IReadOnlyDictionary<string, object?> map => DescribeArgs(map),
        List<object?> list => "[" + string.Join(", ", list.Select(DescribeValue)) + "]",
        _ => value.ToString() ?? "null"
    };

    private static string StatusName(BehaviourStatus status) => status switch
    {
        BehaviourStatus.Success => "success",
        BehaviourStatus.Failure => "failure",
        BehaviourStatus.Running => "running",
        _ => "cancelled"
    };

    private bool Remove(string plugin, string instanceId, out Brain brain)
    {
        brain = null!;
        return _brainsByPlugin.TryGetValue(plugin, out var brains) &&
            brains.Remove(instanceId, out brain!);
    }

    private Brain Require(string plugin, string instanceId, string what)
    {
        if (_brainsByPlugin.TryGetValue(plugin, out var brains) && brains.TryGetValue(instanceId, out var brain))
        {
            return brain;
        }

        throw new InvalidOperationException($"{what} {instanceId} does not exist for plugin '{plugin}'.");
    }
}
