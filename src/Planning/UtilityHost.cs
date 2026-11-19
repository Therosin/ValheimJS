using System;
using System.Collections.Generic;
using System.Linq;
using Jint;
using Jint.Native;
using Jint.Native.Function;
using Jint.Native.Array;
using Jint.Native.Object;
using Jint.Runtime;
using ValheimJS.AI;
using ValheimJS.Scripting.Binding;

namespace ValheimJS.Planning;

internal sealed class UtilityOptionsData
{
    internal double MaxStep { get; private set; } = 0.02;
    internal int MaxPrerequisiteAttempts { get; private set; } = 2;
    internal double PrerequisiteCooldownMs { get; private set; } = 30_000;
    internal List<string> Considerations { get; } = new();
    internal List<string> FeedbackRules { get; } = new();
    internal Dictionary<string, double> Stats { get; } = new(StringComparer.Ordinal);
    internal PlannerOptionsData Planner { get; private set; } = new();

    internal static UtilityOptionsData Parse(JsValue value)
    {
        var options = new UtilityOptionsData();
        if (value.IsNull() || value.IsUndefined())
        {
            return options;
        }

        if (value is not ObjectInstance obj)
        {
            throw new ArgumentException("utility options must be an object.");
        }

        options.MaxStep = PlanOptionsData.ReadNumber(obj, "maxStep") ?? 0.02;
        options.MaxPrerequisiteAttempts = (int)(PlanOptionsData.ReadNumber(obj, "maxPrerequisiteAttempts") ?? 2);
        options.PrerequisiteCooldownMs = PlanOptionsData.ReadNumber(obj, "prerequisiteCooldownMs") ?? 30_000;
        options.Planner = PlannerOptionsData.Parse(obj.Get("planner"));

        if (obj.Get("considerations") is ObjectInstance considerations)
        {
            foreach (var key in considerations.GetOwnPropertyKeys(Types.String))
            {
                options.Considerations.Add(key.ToString());
            }
        }

        var feedbackValue = obj.Get("feedback");
        if (feedbackValue.IsArray())
        {
            var feedback = feedbackValue.AsArray();
            for (var index = 0; index < (int)feedback.Length; index++)
            {
                if (feedback.Get(index) is ObjectInstance rule &&
                    rule.Get("id") is { } id &&
                    id.IsString())
                {
                    options.FeedbackRules.Add(id.AsString());
                }
            }
        }

        foreach (var pair in UtilityMath.FromJs(obj.Get("stats")))
        {
            options.Stats[pair.Key] = pair.Value;
        }

        return options;
    }
}

internal sealed class UtilityHost
{
    private sealed class RecordedEvent
    {
        internal string Name = string.Empty;
        internal JsValue Data = JsValue.Null;
        internal double At;
        internal Dictionary<string, double> Observed = new(StringComparer.Ordinal);
    }

    private readonly string _plugin;
    private readonly string _id;
    private readonly JsValue _actor;
    private readonly JsCallback _dispatch;
    private readonly UtilityOptionsData _options;
    private readonly PlannerHost _planner;
    private readonly PrerequisiteResolver _prerequisites;
    private readonly Dictionary<string, UtilityDescription?> _descriptions = new(StringComparer.Ordinal);
    private readonly List<RecordedEvent> _events = new();
    private Dictionary<string, double> _stats;
    private JsValue _lastView = JsValue.Undefined;
    private JsValue _lastOutcome = JsValue.Null;
    private double _lastNow;
    private string? _activeGoal;
    private double _activeSince;
    private JsValue _activeBefore = JsValue.Undefined;
    private Dictionary<string, double> _activeSnapshot = new(StringComparer.Ordinal);
    private UtilityDescription? _activeDescription;

    internal UtilityHost(
        string plugin,
        string id,
        JsValue actor,
        JsValue options,
        JsCallback dispatch,
        BehaviourService? behaviours = null)
    {
        _plugin = plugin;
        _id = id;
        _actor = actor;
        _dispatch = dispatch;
        _options = UtilityOptionsData.Parse(options);
        var initial = new Dictionary<string, double>(UtilityMath.DefaultStats, StringComparer.Ordinal);
        foreach (var pair in _options.Stats)
        {
            initial[pair.Key] = pair.Value;
        }

        _stats = UtilityMath.Normalize(initial);
        _prerequisites = new PrerequisiteResolver(
            _options.MaxPrerequisiteAttempts,
            _options.PrerequisiteCooldownMs,
            message => Dispatch("debug", message));
        _planner = new PlannerHost(
            plugin,
            id,
            actor,
            _options.Planner,
            dispatch,
            ScoreGoal,
            SelectGoal,
            behaviours)
        {
            EnterHook = OnEnter,
            ExitHook = OnExit
        };
    }

    internal string Id => _id;

    internal PlannerHost Planner => _planner;

    internal void SetGoals(IReadOnlyList<GoalMeta> goals)
    {
        _planner.SetGoals(goals);
        _descriptions.Clear();
        _prerequisites.Reset();
        ClearActive();
    }

    internal string? Tick(JsValue view, double now)
    {
        _lastView = view;
        _lastNow = now;
        return _planner.Tick(view, now);
    }

    internal IReadOnlyList<GoalCandidate> Available(JsValue view, double now)
    {
        _lastView = view;
        _lastNow = now;
        var candidates = _planner.Candidates(view, now);
        var result = new List<GoalCandidate>();
        foreach (var candidate in _prerequisites.Filter(candidates, now))
        {
            if (UtilityMath.Requirements(DescriptionFor(candidate.Id, view, now)).Count == 0)
            {
                result.Add(candidate);
            }
        }

        return result;
    }

    internal Dictionary<string, double> Stats() =>
        new(_stats, StringComparer.Ordinal);

    internal JsValue LastOutcome() => _lastOutcome;

    internal void SetStats(JsValue value)
    {
        foreach (var pair in UtilityMath.FromJs(value))
        {
            _stats[pair.Key] = UtilityMath.Clamp01(pair.Value);
        }

        _stats = UtilityMath.Normalize(_stats);
        Dispatch("save", StatsPayload());
    }

    internal bool Record(string name, JsValue data, JsValue view)
    {
        if (_activeGoal == null)
        {
            return false;
        }

        var current = view.IsUndefined() || view.IsNull() ? _lastView : view;
        if (current.IsUndefined() || current.IsNull())
        {
            return false;
        }

        _lastView = current;
        _events.Add(new RecordedEvent
        {
            Name = name,
            Data = data,
            At = _lastNow,
            Observed = UtilityMath.ObserveView(current)
        });
        return true;
    }

    internal void Request(string goalId, double until)
    {
        _prerequisites.Reset();
        _planner.Request(goalId, until);
    }

    internal void Reset()
    {
        _planner.Reset();
        _prerequisites.Reset();
        _descriptions.Clear();
        _lastView = JsValue.Undefined;
        _lastOutcome = JsValue.Null;
        ClearActive();
    }

    internal void Dispose()
    {
        Reset();
    }

    internal void SetPlan(string? planId, PlanHost? plan) => _planner.SetPlan(planId, plan);

    private double? ScoreGoal(GoalMeta goal, JsValue view, double now)
    {
        var description = DescriptionFor(goal.Id, view, now, refresh: true);
        if (description == null)
        {
            var score = Dispatch("score", goal.Id, view, now);
            return score.IsNumber() ? score.AsNumber() : null;
        }

        var total = description.Base;
        foreach (var consideration in _options.Considerations)
        {
            var value = Dispatch(
                "consideration",
                consideration,
                goal.Id,
                description.Original,
                view,
                now,
                StatsPayload());
            if (value.IsNumber() && double.IsFinite(value.AsNumber()))
            {
                total += value.AsNumber();
            }
        }

        return total;
    }

    private string? SelectGoal(IReadOnlyList<GoalCandidate> candidates, JsValue view, double now)
    {
        var chosen = _prerequisites.Select(
            candidates,
            now,
            id => DescriptionFor(id, view, now),
            pool => UtilityMath.PickCandidate(pool, Stat("Predictability"), Random()));
        if (chosen == null)
        {
            return null;
        }

        var summary = string.Join(
            " ",
            candidates.OrderByDescending(candidate => candidate.Score)
                .Take(6)
                .Select(candidate => $"{candidate.Id}={candidate.Score:0.0}"));
        Dispatch("debug", $"[select] {chosen.Value.Id}{(summary.Length > 0 ? " | " + summary : string.Empty)}");

        return chosen.Value.Id;
    }

    private UtilityDescription? DescriptionFor(
        string goalId,
        JsValue view,
        double now,
        bool refresh = false)
    {
        if (!refresh && _descriptions.TryGetValue(goalId, out var cached))
        {
            return cached;
        }

        var value = Dispatch("describe", goalId, view, now, StatsPayload());
        var description = UtilityDescription.Parse(value);
        _descriptions[goalId] = description;
        return description;
    }

    private void OnEnter(GoalMeta goal, JsValue view, double now)
    {
        _activeGoal = goal.Id;
        _activeDescription = DescriptionFor(goal.Id, view, now);
        _activeSince = now;
        _activeBefore = view;
        _activeSnapshot = Snapshot(goal.Id, view, now);
        _lastView = view;
        _lastNow = now;
        _events.Clear();
    }

    private void OnExit(GoalMeta goal, JsValue view, double now, string reason)
    {
        if (_activeGoal == null)
        {
            return;
        }

        try
        {
            var durationMs = Math.Max(0, now - _activeSince);
            var after = Snapshot(goal.Id, view, now);
            var observed = UtilityMath.SnapshotDelta(_activeSnapshot, after);
            foreach (var pair in UtilityMath.TransitionFacts(_activeBefore, view, durationMs))
            {
                observed[pair.Key] = pair.Value;
            }

            var outcome = Outcome(goal.Id, reason, durationMs, view, observed);
            _lastOutcome = outcome;
            Dispatch("debug",
                $"[settle] {goal.Id} {reason} {(durationMs / 1000):0.0}s " +
                $"dist={(observed.TryGetValue("distance", out var distance) ? distance : 0):0.0} " +
                $"hp={(observed.TryGetValue("healthDelta", out var health) ? health : 0):0.0} " +
                $"food={(observed.TryGetValue("foodDelta", out var food) ? food : 0):0.0} events={_events.Count}");

            _prerequisites.Settled(goal.Id, reason, now);

            var adjustments = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var rule in _options.FeedbackRules)
            {
                JsValue result;
                try
                {
                    result = Dispatch("feedback", rule, outcome);
                }
                catch (Exception exception)
                {
                    Dispatch("log", $"utility feedback '{rule}' failed: {exception.Message}");
                    continue;
                }

                foreach (var pair in UtilityMath.FromJs(result))
                {
                    adjustments[pair.Key] = adjustments.TryGetValue(pair.Key, out var current)
                        ? current + pair.Value
                        : pair.Value;
                }
            }

            var applied = UtilityMath.Adjust(_stats, adjustments, _options.MaxStep);
            if (applied.Changed)
            {
                _stats = applied.Next;
                Dispatch("save", StatsPayload());
                Dispatch("debug", $"[feedback] {goal.Id} -> {FormatStats(_stats)}");
            }
        }
        finally
        {
            ClearActive();
        }
    }

    private JsValue Outcome(
        string goalId,
        string reason,
        double durationMs,
        JsValue after,
        IReadOnlyDictionary<string, double> observed)
    {
        var description = _activeDescription?.Original ?? JsValue.Null;
        var expected = _activeDescription == null
            ? JsValue.Null
            : JsPayload.Object(_dispatch.Engine)
                .Set("factors", JsPlain.FromPlain(_dispatch.Engine, _activeDescription.Factors))
                .Set("satisfies", JsPlain.FromPlain(_dispatch.Engine, _activeDescription.Satisfies))
                .Build();

        var events = JsPayload.Array(
            _dispatch.Engine,
            _events.Select(item => (JsValue?)JsPayload.Object(_dispatch.Engine)
                .Set("name", item.Name)
                .Set("data", item.Data)
                .Set("at", item.At)
                .Set("observed", JsPlain.FromPlain(_dispatch.Engine, item.Observed))
                .Build()));

        return JsPayload.Object(_dispatch.Engine)
            .Set("goal", JsPayload.Object(_dispatch.Engine).Set("id", goalId).Build())
            .Set("description", description)
            .Set("expected", expected)
            .Set("status", reason)
            .Set("durationMs", durationMs)
            .Set("before", _activeBefore)
            .Set("after", after)
            .Set("stats", StatsPayload())
            .Set("observed", JsPlain.FromPlain(_dispatch.Engine, observed))
            .Set("events", events)
            .Set("planStatus", _planner.Plan?.Status ?? null)
            .Build();
    }

    private Dictionary<string, double> Snapshot(string goalId, JsValue view, double now)
    {
        var value = Dispatch(
            "snapshot",
            goalId,
            view,
            now,
            null,
            _planner.Plan?.Status,
            _planner.Plan?.Reason,
            _planner.Plan?.StepIndex ?? 0);
        return UtilityMath.FromJs(value);
    }

    private JsValue StatsPayload() => JsPlain.FromPlain(_dispatch.Engine, _stats);

    private double Stat(string key) => _stats.TryGetValue(key, out var value) ? value : 0.5;

    private double Random()
    {
        var value = Dispatch("random");
        return value.IsNumber() && double.IsFinite(value.AsNumber())
            ? UtilityMath.Clamp01(value.AsNumber())
            : 0.5;
    }

    private void ClearActive()
    {
        _activeGoal = null;
        _activeDescription = null;
        _activeSnapshot = new Dictionary<string, double>(StringComparer.Ordinal);
        _events.Clear();
    }

    private JsValue Dispatch(string kind, params object?[] args)
    {
        var values = new object?[args.Length + 1];
        values[0] = kind;
        Array.Copy(args, 0, values, 1, args.Length);
        return _dispatch.Invoke(values);
    }

    private static string FormatStats(IReadOnlyDictionary<string, double> stats) =>
        string.Join(" ", stats.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={pair.Value:0.00}"));
}
