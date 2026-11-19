using System;
using System.Collections.Generic;
using System.Linq;
using Jint;
using Jint.Native;
using Jint.Native.Function;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Interop;
using ValheimJS.AI;
using ValheimJS.Core;
using ValheimJS.Scripting.Binding;
using ValheimJS.Scripting.Types;

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
internal sealed class AiBindings : JsBindings
{
    private readonly Dictionary<string, Blackboard> _boards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<int, BoardSubscription>> _boardSubscriptions = new(StringComparer.Ordinal);
    private int _nextBoardSubscription;

    private sealed class BoardSubscription
    {
        internal Action Remove = () => { };
    }

    public AiBindings(JsBindingContext context) : base(context)
    {
    }

    [JsBinding(Scoped = true, Description = "Enables host-side AI tracing for this plugin (tree nodes, actions, conditions and imports).")]
    public bool Debug(string plugin, bool enabled) => Context.Behaviours.Debug(plugin, enabled);

    [JsBinding("boardGet", Scoped = true, ReturnKind = JsReturnKind.Native, Returns = "unknown",
        Description = "Reads a blackboard scope. Empty channel is this plugin's local root; @local/<name> is a local channel; @shared or @shared/<name> is the world-shared board.")]
    public JsValue BoardGet(string plugin, string key, [JsNullable] string? channel) =>
        Board(plugin, channel).TryGetRaw(key, out var value) ? ToJs(value) : JsValue.Null;

    [JsBinding("boardSet", Scoped = true,
        Description = "Sets a blackboard scope value. See boardGet for scope syntax.")]
    public void BoardSet(string plugin, string key, JsValue value, [JsNullable] string? channel) =>
        Board(plugin, channel).Set(key, FromJs(value));

    [JsBinding("boardHas", Scoped = true, Description = "True when the blackboard scope contains the key.")]
    public bool BoardHas(string plugin, string key, [JsNullable] string? channel) =>
        Board(plugin, channel).Contains(key);

    [JsBinding("boardSnapshot", Scoped = true, ReturnKind = JsReturnKind.Native, Returns = "Record<string, unknown>",
        Description = "Copies all state from a blackboard scope.")]
    public JsValue BoardSnapshot(string plugin, [JsNullable] string? channel) =>
        ToJsMap(Board(plugin, channel).Snapshot());

    [JsBinding("boardEmit", Scoped = true,
        Description = "Emits a named signal on a blackboard scope; only that scope's listeners receive it.")]
    public void BoardEmit(string plugin, string name, JsValue data, [JsNullable] string? channel) =>
        Board(plugin, channel).Emit(name, FromJs(data));

    [JsBinding("boardOnChange", Scoped = true,
        Description = "Subscribes to blackboard state changes; empty key means every key. Returns a subscription id.")]
    public int BoardOnChange(string plugin, string key, JsCallback callback, [JsNullable] string? channel)
    {
        var scope = Board(plugin, channel);
        var subscription = new BoardSubscription();
        Action<BlackboardChange> handler = change =>
        {
            if (key.Length > 0 && !string.Equals(change.Key, key, StringComparison.Ordinal)) return;
            InvokeBoard(plugin, callback, "change", change.Key, change.NewValue, change.OldValue, scope.ChannelName);
        };

        scope.Changed += handler;
        subscription.Remove = () => scope.Changed -= handler;
        return RegisterBoardSubscription(plugin, subscription);
    }

    [JsBinding("boardOnSignal", Scoped = true,
        Description = "Subscribes to blackboard signals; empty name means every signal. Returns a subscription id.")]
    public int BoardOnSignal(string plugin, string name, JsCallback callback, [JsNullable] string? channel)
    {
        var scope = Board(plugin, channel);
        var subscription = new BoardSubscription();
        Action<BlackboardSignal> handler = signal =>
        {
            if (name.Length > 0 && !string.Equals(signal.Name, name, StringComparison.Ordinal)) return;
            InvokeBoard(plugin, callback, "signal", signal.Name, signal.Data, scope.ChannelName);
        };

        scope.Signaled += handler;
        subscription.Remove = () => scope.Signaled -= handler;
        return RegisterBoardSubscription(plugin, subscription);
    }

    [JsBinding("boardOff", Scoped = true, Description = "Cancels a blackboard change/signal/middleware subscription.")]
    public bool BoardOff(string plugin, int subscription)
    {
        if (!_boardSubscriptions.TryGetValue(plugin, out var subscriptions) ||
            !subscriptions.Remove(subscription, out var entry))
        {
            return false;
        }

        entry.Remove();
        return true;
    }

    [JsBinding("boardUse", Scoped = true,
        Description = "Registers reactive middleware or a pre-commit interceptor. The callback receives an operation context with get/set/emit/cancel/replace. Returns a subscription id.")]
    public int BoardUse(string plugin, JsCallback callback, [JsNullable] string? channel, bool interceptor)
    {
        var scope = Board(plugin, channel);
        var subscription = new BoardSubscription();
        Action<BlackboardContext> handler = context => InvokeBoardMiddleware(plugin, callback, context);
        var registration = interceptor ? scope.UseInterceptor(handler) : scope.Use(handler);
        subscription.Remove = registration.Dispose;
        return RegisterBoardSubscription(plugin, subscription);
    }

    [JsBinding("boardDrop", Scoped = true, Description = "Drops a local channel created by this plugin. Shared channels cannot be dropped.")]
    public bool BoardDrop(string plugin, [JsNullable] string? channel)
    {
        if (string.IsNullOrWhiteSpace(channel) ||
            string.Equals(channel, LocalScope, StringComparison.Ordinal) ||
            string.Equals(channel, SharedScope, StringComparison.Ordinal) ||
            channel.StartsWith(SharedScopePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var name = channel.StartsWith(LocalScopePrefix, StringComparison.Ordinal)
            ? channel.Substring(LocalScopePrefix.Length)
            : channel;
        return !string.IsNullOrWhiteSpace(name) && LocalBoard(plugin).Channels.Remove(name);
    }

    private const string SharedScope = "@shared";
    private const string SharedScopePrefix = "@shared/";
    private const string LocalScope = "@local";
    private const string LocalScopePrefix = "@local/";

    private BlackboardScope Board(string plugin, string? channel)
    {
        var local = LocalBoard(plugin);

        if (string.IsNullOrWhiteSpace(channel) || string.Equals(channel, LocalScope, StringComparison.Ordinal))
        {
            return local;
        }

        if (string.Equals(channel, SharedScope, StringComparison.Ordinal))
        {
            return Blackboard.Global;
        }

        if (channel.StartsWith(SharedScopePrefix, StringComparison.Ordinal))
        {
            return Blackboard.Global.Channels.Get(channel.Substring(SharedScopePrefix.Length));
        }

        if (channel.StartsWith(LocalScopePrefix, StringComparison.Ordinal))
        {
            return local.Channels.Get(channel.Substring(LocalScopePrefix.Length));
        }

        return local.Channels.Get(channel);
    }

    private Blackboard LocalBoard(string plugin)
    {
        if (!_boards.TryGetValue(plugin, out var board))
        {
            board = new Blackboard();
            _boards[plugin] = board;
        }

        return board;
    }

    private int RegisterBoardSubscription(string plugin, BoardSubscription subscription)
    {
        if (!_boardSubscriptions.TryGetValue(plugin, out var subscriptions))
        {
            subscriptions = new Dictionary<int, BoardSubscription>();
            _boardSubscriptions[plugin] = subscriptions;
        }

        var id = ++_nextBoardSubscription;
        subscriptions[id] = subscription;
        return id;
    }

    private void InvokeBoard(string plugin, JsCallback callback, string kind, params object?[] values)
    {
        try
        {
            callback.Invoke(values.Select(ToJs).ToArray());
        }
        catch (Exception exception)
        {
            LogWarn($"[{plugin}] ai.board {kind} callback failed: {exception.Message}");
        }
    }

    private void InvokeBoardMiddleware(string plugin, JsCallback callback, BlackboardContext context)
    {
        try
        {
            callback.Invoke(MiddlewareContext(context));
        }
        catch (Exception exception)
        {
            LogWarn($"[{plugin}] ai.board middleware failed on '{context.Key}': {exception.Message}");
        }
    }

    private JsValue MiddlewareContext(BlackboardContext context)
    {
        JsValue ReadKey(JsValue[] args) =>
            args.Length > 0 && args[0].IsString()
                ? JsValue.FromObject(Engine, args[0].AsString())
                : JsValue.Undefined;

        string Key(JsValue[] args) =>
            args.Length > 0 && args[0].IsString()
                ? args[0].AsString()
                : throw new BehaviourException("ai.board middleware key must be a string.");

        return JsPayload.Object(Engine)
            .Set("kind", context.Kind == BlackboardOperationKind.Change ? "change" : "signal")
            .Set("key", context.Key)
            .Set("value", ToJs(context.Value))
            .Set("oldValue", ToJs(context.OldValue))
            .Set("channel", context.ChannelName)
            .Set("scope", ReferenceEquals(context.Scope.Owner, Blackboard.Global) ? "shared" : "local")
            .Set("isChange", new ClrFunction(Engine, "isChange", (_, args) =>
            {
                var key = ReadKey(args);
                return key.IsUndefined() ? context.Kind == BlackboardOperationKind.Change : context.IsChange(key.AsString());
            }))
            .Set("isEvent", new ClrFunction(Engine, "isEvent", (_, args) =>
            {
                var name = ReadKey(args);
                return name.IsUndefined() ? context.Kind == BlackboardOperationKind.Signal : context.IsEvent(name.AsString());
            }))
            .Set("get", new ClrFunction(Engine, "get", (_, args) =>
                ToJs(context.GetOrDefault<object?>(Key(args), null))))
            .Set("has", new ClrFunction(Engine, "has", (_, args) =>
                context.Scope.Contains(Key(args))))
            .Set("set", new ClrFunction(Engine, "set", (_, args) =>
            {
                context.Set(Key(args), FromJs(args.Length > 1 ? args[1] : JsValue.Null));
                return JsValue.Undefined;
            }))
            .Set("emit", new ClrFunction(Engine, "emit", (_, args) =>
            {
                context.Emit(Key(args), FromJs(args.Length > 1 ? args[1] : JsValue.Null));
                return JsValue.Undefined;
            }))
            .Set("cancel", new ClrFunction(Engine, "cancel", (_, _) =>
            {
                context.Cancel();
                return JsValue.Undefined;
            }))
            .Set("replace", new ClrFunction(Engine, "replace", (_, args) =>
            {
                context.Replace(FromJs(args.Length > 0 ? args[0] : JsValue.Null));
                return JsValue.Undefined;
            }))
            .Build();
    }

    [JsBinding(Scoped = true, Description = "Compiles YAML into a tree id. Imports come from options.imports keyed by alias.")]
    public string Compile(string plugin, string yaml, JsValue options) =>
        Context.Behaviours.Compile(plugin, yaml, options);

    [JsBinding(Scoped = true, Description = "Creates an independent instance of a compiled tree; returns its id.")]
    public string Instantiate(string plugin, string treeId, JsValue options) =>
        Context.Behaviours.Instantiate(plugin, treeId, options);

    [JsBinding(Scoped = true, ReturnKind = JsReturnKind.Native, Returns = "AiTickResult",
        Description = "Advances one instance; status is success, failure, running or cancelled.")]
    public JsValue Tick(string plugin, string instanceId, double deltaTime) =>
        Context.Behaviours.Tick(plugin, instanceId, deltaTime);

    [JsBinding(Scoped = true, ReturnKind = JsReturnKind.Native, Returns = "Record<string, unknown>",
        Description = "Deep copy of the instance state.")]
    public JsValue State(string plugin, string instanceId) =>
        Context.Behaviours.State(plugin, instanceId);

    [JsBinding(Scoped = true, Description = "Replaces one top-level state value.")]
    public void Set(string plugin, string instanceId, string key, JsValue value) =>
        Context.Behaviours.Set(plugin, instanceId, key, value);

    [JsBinding(Scoped = true, Description = "Current status of the last tick, or cancelled.")]
    public string Status(string plugin, string instanceId) =>
        Context.Behaviours.Status(plugin, instanceId);

    [JsBinding(Scoped = true, Description = "Stops the instance and runs active execAfter hooks as interrupted.")]
    public void Cancel(string plugin, string instanceId) =>
        Context.Behaviours.Cancel(plugin, instanceId);

    [JsBinding(Scoped = true, Description = "Clears execution progress; optionally restores declared state defaults.")]
    public void Reset(string plugin, string instanceId, bool resetState) =>
        Context.Behaviours.Reset(plugin, instanceId, resetState);

    [JsBinding(Scoped = true, Description = "Drops one instance without running hooks.")]
    public bool Release(string plugin, string instanceId) =>
        Context.Behaviours.Release(plugin, instanceId);

    [JsBinding(Scoped = true, Description = "Drops a compiled tree and every instance using it.")]
    public bool Unload(string plugin, string treeId) =>
        Context.Behaviours.Unload(plugin, treeId);

    public override void Release(string plugin)
    {
        Context.Behaviours.ReleasePlugin(plugin);
        if (_boardSubscriptions.Remove(plugin, out var subscriptions))
        {
            foreach (var subscription in subscriptions.Values)
            {
                subscription.Remove();
            }
        }

        _boards.Remove(plugin);
    }

    public override void Dispose()
    {
        foreach (var subscriptions in _boardSubscriptions.Values)
        {
            foreach (var subscription in subscriptions.Values)
            {
                subscription.Remove();
            }
        }

        _boardSubscriptions.Clear();
        _boards.Clear();
        base.Dispose();
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

    private JsValue ToJs(object? value) => JsPlain.ToJs(Engine, value);

    private static object? FromJs(JsValue value) => JsPlain.From(value);

}
