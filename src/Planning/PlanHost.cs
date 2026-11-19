using System;
using Jint;
using System.Collections.Generic;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using ValheimJS.AI;
using ValheimJS.Scripting.Binding;

namespace ValheimJS.Planning;

/// <summary>
/// Owns one plan's step cursor, timeouts, retries and native-action settlement. JavaScript only
/// supplies step data and the dispatch callback for non-native steps.
/// </summary>
internal sealed class PlanHost
{
    private static readonly HashSet<string> NativeActions = new(StringComparer.Ordinal)
    {
        "moveTo", "wait", "lookAt", "face", "pick", "attack", "jump", "interact", "harvest",
        "stop", "idle", "take", "store", "withdraw", "cook", "use", "crouch", "follow"
    };

    private static readonly HashSet<string> ScriptActions = new(StringComparer.Ordinal)
    {
        "until", "call", "equip", "unequip", "consume", "drop", "craft"
    };

    private readonly string _plugin;
    private readonly string _id;
    private readonly JsValue _steps;
    private readonly PlanOptionsData _options;
    private readonly JsCallback _dispatch;
    private readonly IDictionary<string, object?> _nativeState;
    private readonly Dictionary<string, object?> _state = new(StringComparer.Ordinal);
    private Dictionary<string, object?> _local = new(StringComparer.Ordinal);

    private string _status = "idle";
    private string? _reason;
    private int _index;
    private double _startedAt;
    private double _stepStartedAt;
    private int _attempt;
    private int _approachAttempts;
    private bool _approachRetry;
    private int _interactRetries;
    private double? _followStillAt;
    private double _followX;
    private double _followZ;
    private double _retryAt;
    private double? _pausedAt;

    internal PlanHost(
        string plugin,
        string id,
        JsValue actor,
        JsValue steps,
        PlanOptionsData options,
        JsCallback dispatch,
        IDictionary<string, object?> nativeState)
    {
        _plugin = plugin;
        _id = id;
        _steps = steps;
        _options = options;
        _dispatch = dispatch;
        _nativeState = nativeState;
        Actor = actor;
    }

    internal JsValue Actor { get; }

    internal string Status => _status;

    internal string? Reason => _reason;

    internal int StepIndex => _index;

    internal bool IsTerminal =>
        _status is "success" or "failure" or "cancelled";

    internal string Tick(JsValue view, double now)
    {
        if (_status == "idle")
        {
            _status = "running";
            _startedAt = now;
            _stepStartedAt = now;
        }

        if (_status != "running")
        {
            return _status;
        }

        if (_options.TimeoutMs is { } timeout && now - _startedAt > timeout)
        {
            return Fail("plan-timeout", now);
        }

        if (now < _retryAt)
        {
            return _status;
        }

        var steps = _steps.IsArray() ? _steps.AsArray() : null;
        if (steps == null)
        {
            return Fail("unknown-action", now);
        }

        if (_index >= (int)steps.Length)
        {
            _status = "success";
            return _status;
        }

        var step = steps.Get(_index);
        if (step is not ObjectInstance obj)
        {
            return Fail("unknown-action", now);
        }

        if (PlanOptionsData.ReadNumber(obj, "timeoutMs") is { } stepTimeout &&
            now - _stepStartedAt > stepTimeout)
        {
            return Fail("step-timeout", now);
        }

        var action = ResolveAction(obj);
        if (action == null)
        {
            return Fail("unknown-action", now);
        }

        if (action.Value.Native)
        {
            var native = AiNative.Resolve;
            if (native == null)
            {
                return Fail("no-native-resolver", now);
            }

            var handle = ReadViewId(view);
            if (string.IsNullOrWhiteSpace(handle))
            {
                return Fail("no-view", now);
            }

            var args = action.Value.Args;
            if (action.Value.Name == "follow")
            {
                return TickFollow(obj, view, handle, now);
            }

            if (action.Value.Name is "interact" or "harvest")
            {
                var approachStatus = TickApproach(obj, action.Value.Name, view, handle, now);
                if (approachStatus != null)
                {
                    return approachStatus;
                }
            }

            var result = native(
                _plugin,
                handle,
                $"{_id}:{_index}:{_attempt}",
                action.Value.Name,
                args,
                _nativeState);

            if (result.Handled)
            {
                if (result.Running)
                {
                    if (result.Paused)
                    {
                        _pausedAt ??= now;
                        if (_options.PauseGraceMs is { } grace && now - _pausedAt.Value > grace)
                        {
                            return Fail(NativeReason(result.Output) ?? "paused", now);
                        }
                    }
                    else
                    {
                        _pausedAt = null;
                    }

                    return _status;
                }

                _pausedAt = null;
                if (result.Success)
                {
                    _interactRetries = 0;
                    return Advance(now);
                }

                var reason = NativeReason(result.Output) ?? "action-failed";
                if (reason is "not-owner" or "not-loaded" && _interactRetries < 30)
                {
                    _interactRetries++;
                    return _status;
                }

                if (reason == "out-of-range" && action.Value.Name is "interact" or "harvest")
                {
                    var retry = TickApproach(obj, action.Value.Name, view, handle, now, force: true);
                    if (retry != null)
                    {
                        return retry;
                    }
                }

                return Fail(reason, now);
            }
        }

        var state = BuildStateObject(_state);
        var local = BuildStateObject(_local);
        var dispatchResult = _dispatch.Invoke(
            "step",
            step,
            view,
            now,
            state,
            local);
        ReplaceState(state, _state);
        ReplaceState(local, _local);
        return Apply(dispatchResult, now);
    }

    internal void Cancel(string? reason = null)
    {
        if (IsTerminal)
        {
            return;
        }

        _status = "cancelled";
        _reason = string.IsNullOrWhiteSpace(reason) ? "cancelled" : reason;
        _pausedAt = null;
        _nativeState.Clear();
    }

    internal JsValue Snapshot() =>
        JsPayload.Object(_dispatch.Engine)
            .Set("status", _status)
            .Set("reason", _reason)
            .Set("index", _index)
            .Build();

    private string Apply(JsValue outcome, double now)
    {
        if (outcome.IsString())
        {
            return ApplyStatus(outcome.AsString(), null, now);
        }

        if (outcome is ObjectInstance obj)
        {
            var status = obj.Get("status");
            if (!status.IsString())
            {
                return Fail("invalid-outcome", now);
            }

            var reason = obj.Get("reason");
            return ApplyStatus(
                status.AsString(),
                reason.IsString() ? reason.AsString() : null,
                now);
        }

        return Fail("invalid-outcome", now);
    }

    private string ApplyStatus(string status, string? reason, double now)
    {
        switch (status)
        {
            case "running":
                return _status;
            case "success":
                return Advance(now);
            case "failure":
                return Fail(reason ?? "action-failed", now);
            default:
                return Fail("invalid-outcome", now);
        }
    }

    private string Advance(double now)
    {
        _index++;
        _stepStartedAt = now;
        _attempt = 0;
        _approachAttempts = 0;
        _approachRetry = false;
        _interactRetries = 0;
        _followStillAt = null;
        _retryAt = 0;
        _pausedAt = null;
        _local = new Dictionary<string, object?>(StringComparer.Ordinal);
        _nativeState.Clear();
        if (_index >= (_steps.IsArray() ? (int)_steps.AsArray().Length : 0))
        {
            _status = "success";
        }

        return _status;
    }

    private string? TickApproach(
        ObjectInstance step,
        string action,
        JsValue view,
        string handle,
        double now,
        bool force = false)
    {
        if (force)
        {
            _approachRetry = true;
        }

        var config = step.Get(action) as ObjectInstance;
        if (config == null)
        {
            return null;
        }

        var approach = config.Get("approach");
        var enabled = true;
        var distance = 3d;
        var run = true;
        if (approach.IsBoolean())
        {
            enabled = approach.AsBoolean();
        }
        else if (approach is ObjectInstance approachObject)
        {
            distance = PlanOptionsData.ReadNumber(approachObject, "distance") ?? distance;
            var runValue = approachObject.Get("run");
            run = !runValue.IsBoolean() || runValue.AsBoolean();
        }

        if (!enabled)
        {
            return null;
        }

        var target = ReadTarget(config, view);
        if (target == null)
        {
            return Fail("target-missing", now);
        }

        if (!_approachRetry && target.Value.Distance <= distance)
        {
            _approachAttempts = 0;
            return null;
        }

        if (_approachAttempts >= 3)
        {
            return Fail("out-of-range", now);
        }

        _approachAttempts++;
        var native = AiNative.Resolve;
        if (native == null)
        {
            return Fail("no-native-resolver", now);
        }

        var args = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["target"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["x"] = target.Value.X,
                ["y"] = target.Value.Y,
                ["z"] = target.Value.Z
            },
            ["run"] = run
        };
        var result = native(
            _plugin,
            handle,
            $"{_id}:{_index}:{_attempt}:approach",
            "moveTo",
            args,
            _nativeState);
        if (result.Handled && result.Running)
        {
            return _status;
        }

        if (result.Handled && !result.Success)
        {
            return Fail(NativeReason(result.Output) ?? "approach-failed", now);
        }

        _approachRetry = false;
        return null;
    }

    private string TickFollow(ObjectInstance step, JsValue view, string handle, double now)
    {
        if (step.Get("follow") is not ObjectInstance config)
        {
            return Fail("target-required", now);
        }

        var target = ReadTarget(config, view);
        if (target == null)
        {
            return Fail("target-lost", now);
        }

        var distance = PlanOptionsData.ReadNumber(config, "distance") ?? 4;
        if (target.Value.Distance <= distance)
        {
            // A moving target can pass through the range. Complete only after it remains
            // near-stationary for a short grace period; the behaviour layer decides whether to
            // invoke follow again.
            if (_followStillAt == null ||
                Math.Abs(target.Value.X - _followX) > 0.15 ||
                Math.Abs(target.Value.Z - _followZ) > 0.15)
            {
                _followStillAt = now;
            }

            _followX = target.Value.X;
            _followZ = target.Value.Z;
            return now - _followStillAt.Value >= 250 ? Advance(now) : _status;
        }

        _followStillAt = null;
        _followX = target.Value.X;
        _followZ = target.Value.Z;
        var native = AiNative.Resolve;
        if (native == null)
        {
            return Fail("no-native-resolver", now);
        }

        var run = true;
        var runValue = config.Get("run");
        run = !runValue.IsBoolean() || runValue.AsBoolean();
        var result = native(
            _plugin,
            handle,
            $"{_id}:{_index}:{_attempt}:follow",
            "moveTo",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["target"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["x"] = target.Value.X,
                    ["y"] = target.Value.Y,
                    ["z"] = target.Value.Z
                },
                ["run"] = run
            },
            _nativeState);
        if (result.Handled && result.Running)
        {
            return _status;
        }

        if (result.Handled && !result.Success)
        {
            return Fail(NativeReason(result.Output) ?? "follow-failed", now);
        }

        return _status;
    }

    private static TargetInfo? ReadTarget(ObjectInstance config, JsValue view)
    {
        var value = config.Get("target");
        if (value.IsUndefined() || value.IsNull())
        {
            value = config.Get("id");
        }

        if (value.IsUndefined() || value.IsNull())
        {
            return null;
        }

        if (value.IsString())
        {
            return FindSense(view, value.AsString());
        }

        if (JsPlain.From(value) is not IReadOnlyDictionary<string, object?> map)
        {
            return null;
        }

        var id = map.TryGetValue("id", out var rawId) ? rawId as string : null;
        if (id == null)
        {
            return null;
        }

        if (map.TryGetValue("pos", out var rawPos) &&
            rawPos is IReadOnlyDictionary<string, object?> pos)
        {
            return Point(id, pos, map);
        }

        return Point(id, map, map);
    }

    private static TargetInfo? FindSense(JsValue view, string id)
    {
        if (JsPlain.From(view) is not Dictionary<string, object?> map)
        {
            return null;
        }

        foreach (var key in new[] { "nearby", "objects", "remembered" })
        {
            if (map.TryGetValue(key, out var value) && value is List<object?> list)
            {
                foreach (var entry in list)
                {
                    if (entry is not IReadOnlyDictionary<string, object?> item ||
                        !item.TryGetValue("id", out var rawId) ||
                        rawId as string != id)
                    {
                        continue;
                    }

                    if (item.TryGetValue("pos", out var pos) &&
                        pos is IReadOnlyDictionary<string, object?> position)
                    {
                        return Point(id, position, item);
                    }
                }
            }
        }

        return null;
    }

    private static TargetInfo? Point(
        string id,
        IReadOnlyDictionary<string, object?> position,
        IReadOnlyDictionary<string, object?> facts)
    {
        if (!TryNumber(position, "x", out var x) ||
            !TryNumber(position, "y", out var y) ||
            !TryNumber(position, "z", out var z))
        {
            return null;
        }

        var distance = TryNumber(facts, "distance", out var value) ? value : 0;
        return new TargetInfo(id, x, y, z, distance);
    }

    private static bool TryNumber(IReadOnlyDictionary<string, object?> map, string key, out double value)
    {
        value = 0;
        if (!map.TryGetValue(key, out var raw))
        {
            return false;
        }

        if (raw is double number)
        {
            value = number;
            return true;
        }

        if (raw is long integer)
        {
            value = integer;
            return true;
        }

        return false;
    }

    private readonly struct TargetInfo
    {
        internal TargetInfo(string id, double x, double y, double z, double distance)
        {
            Id = id;
            X = x;
            Y = y;
            Z = z;
            Distance = distance;
        }

        internal string Id { get; }
        internal double X { get; }
        internal double Y { get; }
        internal double Z { get; }
        internal double Distance { get; }
    }

    private string Fail(string reason, double now)
    {
        var step = StepAt(_index);
        var retry = RetryOptions.Parse(step);
        if (retry is { } policy && _attempt < policy.Attempts)
        {
            _attempt++;
            _approachAttempts = 0;
            _approachRetry = false;
            _interactRetries = 0;
            _followStillAt = null;
            _retryAt = now + policy.DelayMs;
            _stepStartedAt = now;
            _pausedAt = null;
            _local = new Dictionary<string, object?>(StringComparer.Ordinal);
            _nativeState.Clear();
            return _status;
        }

        _status = "failure";
        _reason = reason;
        _pausedAt = null;
        _nativeState.Clear();
        return _status;
    }

    private (string Name, bool Native, IReadOnlyDictionary<string, object?> Args)? ResolveAction(
        ObjectInstance step)
    {
        string? key = null;
        foreach (var property in step.GetOwnPropertyKeys(Jint.Runtime.Types.String))
        {
            var name = property.ToString();
            if (name is "timeoutMs" or "retry")
            {
                continue;
            }

            key = name;
            break;
        }

        if (key == null)
        {
            return null;
        }

        if (key == "action")
        {
            var action = step.Get("action");
            if (!action.IsString() || string.IsNullOrWhiteSpace(action.AsString()))
            {
                return null;
            }

            var name = action.AsString();
            var native = NativeActions.Contains(name);
            var config = step.Get("config");
            var args = native && config is ObjectInstance configObject
                ? PlainMap(configObject)
                : new Dictionary<string, object?>(StringComparer.Ordinal);
            return (name, native, args);
        }

        var isNative = NativeActions.Contains(key);
        if (!isNative && !ScriptActions.Contains(key))
        {
            return null;
        }

        return (key, isNative, isNative
            ? NativeArgs(step, key)
            : new Dictionary<string, object?>(StringComparer.Ordinal));
    }

    private static IReadOnlyDictionary<string, object?> NativeArgs(ObjectInstance step, string key)
    {
        var args = PlainMap(step);
        args.Remove("timeoutMs");
        args.Remove("retry");

        var value = step.Get(key);
        var plain = JsPlain.From(value);
        switch (key)
        {
            case "moveTo":
            case "lookAt":
            case "face":
            case "pick":
                args["target"] = plain;
                break;
            case "wait":
                args["seconds"] = plain;
                break;
            case "attack":
            case "interact":
            case "harvest":
            case "take":
            case "store":
            case "withdraw":
            case "cook":
            case "use":
                if (plain is IReadOnlyDictionary<string, object?> nested)
                {
                    foreach (var pair in nested)
                    {
                        args[pair.Key] = pair.Value;
                    }
                }
                break;
        }

        return args;
    }

    private static Dictionary<string, object?> PlainMap(ObjectInstance value)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var key in value.GetOwnPropertyKeys(Jint.Runtime.Types.String))
        {
            var name = key.ToString();
            map[name] = JsPlain.From(value.Get(name));
        }

        return map;
    }

    private ObjectInstance? StepAt(int index)
    {
        if (!_steps.IsArray())
        {
            return null;
        }

        var steps = _steps.AsArray();
        return index >= 0 && index < (int)steps.Length && steps.Get(index) is ObjectInstance obj
            ? obj
            : null;
    }

    private JsValue BuildStateObject(IDictionary<string, object?> state) =>
        JsPlain.FromPlain(_dispatch.Engine, state);

    private static void ReplaceState(JsValue value, Dictionary<string, object?> target)
    {
        if (JsPlain.From(value) is not Dictionary<string, object?> snapshot)
        {
            throw new InvalidOperationException("plan action state must stay an object.");
        }

        target.Clear();
        foreach (var pair in snapshot)
        {
            target[pair.Key] = pair.Value;
        }
    }

    private static string? ReadViewId(JsValue view)
    {
        if (view is not ObjectInstance obj)
        {
            return null;
        }

        var id = obj.Get("id");
        return id.IsString() && id.AsString().Length > 0 ? id.AsString() : null;
    }

    private static string? NativeReason(object? output) => output switch
    {
        null => null,
        string text => text,
        IReadOnlyDictionary<string, object?> map when map.TryGetValue("reason", out var reason) =>
            reason as string,
        _ => output.ToString()
    };
}
