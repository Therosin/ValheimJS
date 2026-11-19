using System;
using System.Collections.Generic;
using System.Linq;
using Jint;
using Jint.Native;
using Jint.Native.Function;
using Jint.Native.Object;
using Jint.Runtime;
using ValheimJS.AI;
using ValheimJS.Scripting.Binding;

namespace ValheimJS.Planning;

/// <summary>Goal metadata owned by the host. Behaviour goals add a tree id in phase 3.</summary>
internal sealed class GoalMeta
{
    internal string Id = string.Empty;
    internal double CooldownMs;
    internal double Hysteresis;
    internal double CommitMs;
    internal bool Urgent;
    internal bool HasTick;
    internal string? BehaviourTree;
    internal JsValue BehaviourState = JsValue.Undefined;
    internal JsCallback? BehaviourStateFactory;
    internal JsCallback? BehaviourAction;
    internal JsCallback? BehaviourCondition;
    internal JsCallback? BehaviourExec;
    internal JsCallback? BehaviourRandom;
    internal int BehaviourJumps;
}

internal readonly struct GoalCandidate
{
    internal GoalCandidate(string id, double score, int order)
    {
        Id = id;
        Score = score;
        Order = order;
    }

    internal string Id { get; }
    internal double Score { get; }
    internal int Order { get; }
}

internal sealed class PlannerOptionsData
{
    internal double Threshold { get; private set; }
    internal double ReplanMs { get; private set; } = 500;
    internal bool HasSelect { get; private set; }

    internal static PlannerOptionsData Parse(JsValue value)
    {
        var options = new PlannerOptionsData();
        if (value.IsNull() || value.IsUndefined())
        {
            return options;
        }

        if (value is not ObjectInstance obj)
        {
            throw new ArgumentException("planner options must be an object.");
        }

        options.Threshold = PlanOptionsData.ReadNumber(obj, "threshold") ?? 0;
        options.ReplanMs = PlanOptionsData.ReadNumber(obj, "replanMs") ?? 500;
        options.HasSelect = obj.Get("select") is Function;
        return options;
    }
}

/// <summary>
/// Goal selection, cooldowns, preemption and active-plan ordering. Goal policy stays in JS; every
/// state transition is host-owned.
/// </summary>
internal sealed class PlannerHost
{
    private readonly string _plugin;
    private readonly string _id;
    private readonly JsValue _actor;
    private readonly PlannerOptionsData _options;
    private readonly JsCallback _dispatch;
    private readonly Func<GoalMeta, JsValue, double, double?>? _scoreOverride;
    private readonly Func<IReadOnlyList<GoalCandidate>, JsValue, double, string?>? _selectOverride;
    private readonly BehaviourService? _behaviours;
    private readonly Dictionary<string, GoalMeta> _goals = new(StringComparer.Ordinal);
    private readonly List<string> _order = new();
    private readonly Dictionary<string, double> _cooldowns = new(StringComparer.Ordinal);

    private GoalMeta? _active;
    private PlanHost? _plan;
    private string? _planId;
    private JsValue _lastView = JsValue.Undefined;
    private double _activeSince;
    private double _lastReplan;
    private double _lastNow;
    private double _lastTickNow;
    private string? _behaviourId;
    private (string Id, double Until)? _requested;

    internal PlannerHost(
        string plugin,
        string id,
        JsValue actor,
        PlannerOptionsData options,
        JsCallback dispatch,
        Func<GoalMeta, JsValue, double, double?>? scoreOverride = null,
        Func<IReadOnlyList<GoalCandidate>, JsValue, double, string?>? selectOverride = null,
        BehaviourService? behaviours = null)
    {
        _plugin = plugin;
        _id = id;
        _actor = actor;
        _options = options;
        _dispatch = dispatch;
        _scoreOverride = scoreOverride;
        _selectOverride = selectOverride;
        _behaviours = behaviours;
    }

    internal string? ActiveGoal => _active?.Id;

    internal double ActiveElapsedMs => _active == null ? 0 : _lastNow - _activeSince;

    internal string? ActivePlanId => _planId;

    internal string Id => _id;

    internal Action<GoalMeta, JsValue, double>? EnterHook { get; set; }

    internal Action<GoalMeta, JsValue, double, string>? ExitHook { get; set; }

    internal PlanHost? Plan => _plan;

    internal void SetGoals(IReadOnlyList<GoalMeta> goals)
    {
        if (_active != null)
        {
            Finish("preempted", _lastNow);
        }

        _goals.Clear();
        _order.Clear();
        foreach (var goal in goals)
        {
            _goals[goal.Id] = goal;
            _order.Add(goal.Id);
        }

        _cooldowns.Clear();
        _requested = null;
        SetPlan(null, null);
    }

    internal string? Tick(JsValue view, double now)
    {
        _lastView = view;
        _lastNow = now;

        if (_active != null && now - _lastReplan >= _options.ReplanMs)
        {
            MaybePreempt(view, now);
        }

        if (_active == null)
        {
            Select(view, now);
        }

        _lastReplan = now;
        if (_active == null)
        {
            return null;
        }

        var active = _active;
        var activeId = active.Id;
        var plan = _plan;
        if (plan != null)
        {
            plan.Tick(view, now);
        }

        if (active.BehaviourTree != null && _behaviourId != null && _behaviours != null)
        {
            var delta = _lastTickNow > 0 ? Math.Clamp((now - _lastTickNow) / 1000, 0, 5) : 0.5;
            var status = _behaviours.TickStatus(_plugin, _behaviourId, (float)delta, out _);
            if (status == BehaviourStatus.Success)
            {
                Finish("success", now);
                _lastTickNow = now;
                return activeId;
            }

            if (status == BehaviourStatus.Failure || status == BehaviourStatus.Cancelled)
            {
                Finish("failure", now);
                _lastTickNow = now;
                return activeId;
            }
        }

        var goalStatus = active.HasTick ? DispatchGoalTick(active, view, now) : "running";
        if (goalStatus != "running")
        {
            Finish(goalStatus == "success" ? "success" : "failure", now);
        }

        _lastTickNow = now;
        return activeId;
    }

    internal IReadOnlyList<GoalCandidate> Candidates(JsValue view, double now)
    {
        _lastView = view;
        _lastNow = now;
        return Collect(view, now, null);
    }

    internal void Request(string goalId, double until)
    {
        if (!_goals.ContainsKey(goalId))
        {
            return;
        }

        _requested = (goalId, until);
    }

    internal void SetPlan(string? planId, PlanHost? plan)
    {
        if (_plan != null && !ReferenceEquals(_plan, plan))
        {
            _plan.Cancel("goal-replaced");
        }

        _planId = planId;
        _plan = plan;
    }

    internal void Reset()
    {
        if (_active != null)
        {
            Finish("preempted", _lastNow);
        }

        _cooldowns.Clear();
        _requested = null;
        _lastReplan = 0;
        SetPlan(null, null);
    }

    internal void Dispose()
    {
        Reset();
        _goals.Clear();
        _order.Clear();
    }

    internal void CancelPlan(string reason)
    {
        _plan?.Cancel(reason);
        SetPlan(null, null);
    }

    internal void NotifySettled(string goalId, string outcome, double now)
    {
        if (outcome == "success")
        {
            _cooldowns.Remove(goalId);
        }
    }

    private void MaybePreempt(JsValue view, double now)
    {
        var current = _active!;
        var candidate = Best(view, now, current.Id);
        if (candidate == null)
        {
            return;
        }

        if (now - _activeSince < current.CommitMs && candidate.Value.Order >= 0)
        {
            // Urgent candidates are checked below; ordinary candidates must wait for commitMs.
            if (!IsUrgent(candidate.Value.Id))
            {
                return;
            }
        }

        var currentScore = Score(current, view, now);
        if (candidate.Value.Score > currentScore + current.Hysteresis)
        {
            Dispatch("debug", $"preempt {current.Id}={currentScore:0.0} -> {candidate.Value.Id}={candidate.Value.Score:0.0}");
            Finish("preempted", now);
        }
    }

    private void Select(JsValue view, double now)
    {
        var requested = _requested;
        if (requested != null)
        {
            if (now > requested.Value.Until)
            {
                _requested = null;
            }
            else if (_goals.TryGetValue(requested.Value.Id, out var goal) &&
                     Eligible(goal, view, now))
            {
                Start(goal, view, now);
                return;
            }
        }

        var candidates = Collect(view, now, null);
        if (candidates.Count == 0)
        {
            return;
        }

        string? selected;
        if (_selectOverride != null)
        {
            selected = _selectOverride(candidates, view, now);
        }
        else if (_options.HasSelect)
        {
            selected = SelectScript(candidates, view, now);
        }
        else
        {
            selected = Best(candidates).Id;
        }

        if (selected == null || !_goals.TryGetValue(selected, out var chosen) ||
            !candidates.Any(candidate => candidate.Id == selected))
        {
            return;
        }

        Start(chosen, view, now);
    }

    private void Start(GoalMeta goal, JsValue view, double now)
    {
        SetPlan(null, null);
        _active = goal;
        _activeSince = now;
        _lastReplan = now;
        _requested = null;
        try
        {
            if (goal.BehaviourTree != null && _behaviours != null)
            {
                var state = goal.BehaviourStateFactory?.InvokeOn(_actor) ?? goal.BehaviourState;
                _behaviourId = _behaviours.InstantiateForGoal(
                    _plugin,
                    goal.BehaviourTree,
                    _actor,
                    goal.BehaviourAction,
                    goal.BehaviourCondition,
                    goal.BehaviourExec,
                    goal.BehaviourRandom,
                    state,
                    goal.BehaviourJumps > 0 ? goal.BehaviourJumps : null);
            }

            EnterHook?.Invoke(goal, view, now);
            Dispatch("enter", goal.Id, view, now, null, PlanStatus(), PlanReason(), PlanStepIndex());
        }
        catch (Exception exception)
        {
            Dispatch("log", $"goal '{goal.Id}' enter failed: {exception.Message}");
            Finish("failure", now);
        }
    }

    private void Finish(string reason, double now)
    {
        var goal = _active;
        if (goal == null)
        {
            return;
        }

        _active = null;
        if (goal.CooldownMs > 0)
        {
            _cooldowns[goal.Id] = now + goal.CooldownMs;
        }

        try
        {
            if (!_lastView.IsUndefined() && !_lastView.IsNull())
            {
                Dispatch("exit", goal.Id, _lastView, now, reason, PlanStatus(), PlanReason(), PlanStepIndex());
            }
        }
        catch (Exception exception)
        {
            Dispatch("log", $"goal '{goal.Id}' exit failed: {exception.Message}");
        }
        finally
        {
            if (!_lastView.IsUndefined() && !_lastView.IsNull())
            {
                ExitHook?.Invoke(goal, _lastView, now, reason);
            }

            if (_behaviourId != null && _behaviours != null)
            {
                _behaviours.Release(_plugin, _behaviourId);
                _behaviourId = null;
            }

            SetPlan(null, null);
        }
    }

    private string DispatchGoalTick(GoalMeta goal, JsValue view, double now)
    {
        var result = Dispatch("tick", goal.Id, view, now, null, PlanStatus(), PlanReason(), PlanStepIndex());
        if (!result.IsString())
        {
            Dispatch("log", $"goal '{goal.Id}' tick must return running, success or failure.");
            return "failure";
        }

        return result.AsString() switch
        {
            "running" => "running",
            "success" => "success",
            "failure" => "failure",
            _ => "failure"
        };
    }

    private List<GoalCandidate> Collect(JsValue view, double now, string? exclude)
    {
        var candidates = new List<GoalCandidate>();
        for (var order = 0; order < _order.Count; order++)
        {
            var id = _order[order];
            if (id == exclude || !_goals.TryGetValue(id, out var goal))
            {
                continue;
            }

            if (_cooldowns.TryGetValue(id, out var cooldown) && cooldown > now)
            {
                continue;
            }

            if (!Eligible(goal, view, now))
            {
                continue;
            }

            var score = Score(goal, view, now);
            if (!double.IsFinite(score) || score <= _options.Threshold)
            {
                continue;
            }

            candidates.Add(new GoalCandidate(id, score, order));
        }

        return candidates;
    }

    private GoalCandidate? Best(JsValue view, double now, string? exclude)
    {
        var candidates = Collect(view, now, exclude);
        return candidates.Count == 0 ? null : Best(candidates);
    }

    private static GoalCandidate Best(IReadOnlyList<GoalCandidate> candidates)
    {
        var best = candidates[0];
        for (var index = 1; index < candidates.Count; index++)
        {
            if (candidates[index].Score > best.Score)
            {
                best = candidates[index];
            }
        }

        return best;
    }

    private bool IsUrgent(string id) => _goals.TryGetValue(id, out var goal) && goal.Urgent;

    private bool Eligible(GoalMeta goal, JsValue view, double now)
    {
        var result = Dispatch("eligible", goal.Id, view, now);
        return result.IsUndefined() || result.IsNull() || (result.IsBoolean() && result.AsBoolean());
    }

    private double Score(GoalMeta goal, JsValue view, double now)
    {
        if (_scoreOverride != null)
        {
            return _scoreOverride(goal, view, now) ?? 0;
        }

        var result = Dispatch("score", goal.Id, view, now);
        return result.IsNumber() ? result.AsNumber() : 0;
    }

    private string? SelectScript(IReadOnlyList<GoalCandidate> candidates, JsValue view, double now)
    {
        var options = JsPayload.Array(
            _dispatch.Engine,
            candidates.Select(candidate => (JsValue?)JsPayload.Object(_dispatch.Engine)
                .Set("goal", candidate.Id)
                .Set("score", candidate.Score)
                .Build()));
        var result = Dispatch("select", options, view, now);
        return result.IsString() ? result.AsString() : null;
    }

    private JsValue Dispatch(string kind, params object?[] args)
    {
        var values = new object?[args.Length + 1];
        values[0] = kind;
        Array.Copy(args, 0, values, 1, args.Length);
        return _dispatch.Invoke(values);
    }

    private string? PlanStatus() => _plan?.Status;

    private string? PlanReason() => _plan?.Reason;

    private double PlanStepIndex() => _plan?.StepIndex ?? 0;
}
