using System;
using Jint;
using System.Collections.Generic;
using System.Linq;
using Jint.Native;
using Jint.Native.Object;
using ValheimJS.Scripting.Binding;

namespace ValheimJS.Planning;

/// <summary>Parsed <c>describe()</c> result. The original JS object is retained for callbacks.</summary>
internal sealed class UtilityDescription
{
    internal double Base;
    internal Dictionary<string, double> Factors = new(StringComparer.Ordinal);
    internal List<string> Requires = new();
    internal List<string> Provides = new();
    internal Dictionary<string, double> Satisfies = new(StringComparer.Ordinal);
    internal JsValue Original = JsValue.Null;

    internal static UtilityDescription? Parse(JsValue value)
    {
        if (value.IsNull() || value.IsUndefined() || value is not ObjectInstance obj)
        {
            return null;
        }

        var description = new UtilityDescription { Original = value };
        var baseValue = obj.Get("base");
        description.Base = baseValue.IsNumber() ? baseValue.AsNumber() : 0;

        if (obj.Get("factors") is ObjectInstance factors)
        {
            foreach (var key in factors.GetOwnPropertyKeys(Jint.Runtime.Types.String))
            {
                var number = factors.Get(key.ToString());
                if (number.IsNumber() && double.IsFinite(number.AsNumber()))
                {
                    description.Factors[key.ToString()] = number.AsNumber();
                }
            }
        }

        ReadStrings(obj.Get("requires"), description.Requires);
        ReadStrings(obj.Get("provides"), description.Provides);

        if (obj.Get("satisfies") is ObjectInstance satisfies)
        {
            foreach (var key in satisfies.GetOwnPropertyKeys(Jint.Runtime.Types.String))
            {
                var number = satisfies.Get(key.ToString());
                if (number.IsNumber() && double.IsFinite(number.AsNumber()))
                {
                    description.Satisfies[key.ToString()] = number.AsNumber();
                }
            }
        }

        return description;
    }

    private static void ReadStrings(JsValue value, List<string> target)
    {
        if (!value.IsArray())
        {
            return;
        }

        var array = value.AsArray();
        for (var index = 0; index < (int)array.Length; index++)
        {
            var item = array.Get(index);
            if (item.IsString() && item.AsString().Length > 0 && !target.Contains(item.AsString()))
            {
                target.Add(item.AsString());
            }
        }
    }
}

internal static class UtilityMath
{
    internal static readonly IReadOnlyDictionary<string, double> DefaultStats =
        new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["Intellect"] = 0.5,
            ["Social"] = 0.5,
            ["Curiosity"] = 0.5,
            ["Ambition"] = 0.5,
            ["Patience"] = 0.5,
            ["Predictability"] = 0.5,
            ["Hunter"] = 0.25,
            ["Cook"] = 0.25,
            ["Crafter"] = 0.25,
            ["Explorer"] = 0.25
        };

    internal static Dictionary<string, double> Normalize(IEnumerable<KeyValuePair<string, double>> stats)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var pair in stats)
        {
            if (double.IsFinite(pair.Value))
            {
                result[pair.Key] = Clamp01(pair.Value);
            }
        }

        return result;
    }

    internal static Dictionary<string, double> FromJs(JsValue value)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        if (value is not ObjectInstance obj)
        {
            return result;
        }

        foreach (var key in obj.GetOwnPropertyKeys(Jint.Runtime.Types.String))
        {
            var number = obj.Get(key.ToString());
            if (number.IsNumber() && double.IsFinite(number.AsNumber()))
            {
                result[key.ToString()] = number.AsNumber();
            }
        }

        return result;
    }

    internal static (Dictionary<string, double> Next, Dictionary<string, double> Applied, bool Changed) Adjust(
        IReadOnlyDictionary<string, double> stats,
        IReadOnlyDictionary<string, double> adjustments,
        double maxStep)
    {
        var next = new Dictionary<string, double>(stats, StringComparer.Ordinal);
        var applied = new Dictionary<string, double>(StringComparer.Ordinal);
        var changed = false;
        foreach (var pair in adjustments)
        {
            if (!double.IsFinite(pair.Value))
            {
                continue;
            }

            var delta = Math.Max(-maxStep, Math.Min(maxStep, pair.Value));
            if (Math.Abs(delta) < double.Epsilon)
            {
                continue;
            }

            var current = next.TryGetValue(pair.Key, out var value) ? value : 0.5;
            var updated = Clamp01(current + delta);
            if (Math.Abs(updated - current) < double.Epsilon)
            {
                continue;
            }

            next[pair.Key] = updated;
            applied[pair.Key] = updated - current;
            changed = true;
        }

        return (next, applied, changed);
    }

    internal static double Clamp(double value, double minimum, double maximum) =>
        Math.Max(minimum, Math.Min(maximum, value));

    internal static double Clamp01(double value) => Clamp(value, 0, 1);

    internal static Dictionary<string, double> ObserveView(JsValue value)
    {
        var map = JsPlain.From(value) as IReadOnlyDictionary<string, object?>
            ?? new Dictionary<string, object?>();
        var self = Child(map, "self");
        var nearby = Array(map, "nearby");
        var combat = Child(map, "combat");
        var environment = Child(map, "environment");
        var home = Child(map, "home");
        var health = Number(map, "health");
        var maxHealth = Number(map, "maxHealth");

        return new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["health"] = health,
            ["maxHealth"] = maxHealth,
            ["healthRatio"] = maxHealth > 0 ? health / maxHealth : 0,
            ["stamina"] = Number(self, "stamina"),
            ["staminaRatio"] = Number(self, "staminaRatio"),
            ["food"] = Array(self, "food").Count,
            ["x"] = Number(map, "x"),
            ["y"] = Number(map, "y"),
            ["z"] = Number(map, "z"),
            ["awayFromHome"] = Horizontal(map, home),
            ["combat"] = Bool(combat, "engaged") ? 1 : 0,
            ["isDay"] = Bool(environment, "isDay") ? 1 : 0,
            ["nearbyPlayers"] = nearby.Count(entry =>
                entry is IReadOnlyDictionary<string, object?> item &&
                Bool(item, "isPlayer") &&
                !Bool(item, "isDead"))
        };
    }

    internal static Dictionary<string, double> TransitionFacts(
        JsValue before,
        JsValue after,
        double durationMs)
    {
        var left = ObserveView(before);
        var right = ObserveView(after);
        return new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["healthDelta"] = right["health"] - left["health"],
            ["staminaDelta"] = right["stamina"] - left["stamina"],
            ["foodDelta"] = right["food"] - left["food"],
            ["distance"] = Math.Sqrt(
                Math.Pow(right["x"] - left["x"], 2) + Math.Pow(right["z"] - left["z"], 2)),
            ["awayFromHome"] = right["awayFromHome"],
            ["duration"] = durationMs / 1000
        };
    }

    internal static Dictionary<string, double> SnapshotDelta(
        IReadOnlyDictionary<string, double> before,
        IReadOnlyDictionary<string, double> after)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var key in before.Keys.Concat(after.Keys).Distinct(StringComparer.Ordinal))
        {
            var hasStart = before.TryGetValue(key, out var start);
            var hasEnd = after.TryGetValue(key, out var end);
            if (hasStart && hasEnd)
            {
                result[key] = end - start;
            }
            else if (hasEnd)
            {
                result[key] = end;
            }
        }

        return result;
    }

    internal static IReadOnlyList<string> Requirements(UtilityDescription? description) =>
        description?.Requires ?? new List<string>();

    internal static string AttemptKey(string goalId, IReadOnlyList<string> missing)
    {
        var ordered = missing.OrderBy(item => item, StringComparer.Ordinal);
        return goalId + "\0" + string.Join("\0", ordered);
    }

    internal static GoalCandidate PickCandidate(
        IReadOnlyList<GoalCandidate> candidates,
        double predictability,
        double random)
    {
        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        var sorted = candidates.OrderByDescending(candidate => candidate.Score).ToList();
        var confidence = Clamp01(predictability);
        if (confidence >= 1)
        {
            return sorted[0];
        }

        var slope = 4 * confidence;
        var weights = sorted.Select((_, index) => Math.Exp(-slope * index)).ToArray();
        var total = weights.Sum();
        var roll = random * total;
        for (var index = 0; index < sorted.Count; index++)
        {
            roll -= weights[index];
            if (roll <= 0)
            {
                return sorted[index];
            }
        }

        return sorted[^1];
    }

    private static IReadOnlyDictionary<string, object?> Child(
        IReadOnlyDictionary<string, object?> map,
        string key) =>
        map.TryGetValue(key, out var value) && value is IReadOnlyDictionary<string, object?> child
            ? child
            : new Dictionary<string, object?>();

    private static List<object?> Array(IReadOnlyDictionary<string, object?> map, string key) =>
        map.TryGetValue(key, out var value) && value is List<object?> list ? list : new List<object?>();

    private static double Number(IReadOnlyDictionary<string, object?> map, string key) =>
        map.TryGetValue(key, out var value) && value is double number && double.IsFinite(number)
            ? number
            : 0;

    private static bool Bool(IReadOnlyDictionary<string, object?> map, string key) =>
        map.TryGetValue(key, out var value) && value is bool flag && flag;

    private static double Horizontal(
        IReadOnlyDictionary<string, object?> view,
        IReadOnlyDictionary<string, object?> home) =>
        Math.Sqrt(
            Math.Pow(Number(view, "x") - Number(home, "x"), 2) +
            Math.Pow(Number(view, "z") - Number(home, "z"), 2));
}

/// <summary>Bounded prerequisite attempt/cooldown state owned by the utility host.</summary>
internal sealed class PrerequisiteResolver
{
    private readonly int _maxAttempts;
    private readonly double _cooldownMs;
    private readonly Action<string>? _debug;
    private readonly Dictionary<string, int> _attempts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _retryAt = new(StringComparer.Ordinal);
    private (string GoalId, string ProviderId)? _pending;
    private string? _resumeId;

    internal PrerequisiteResolver(int maxAttempts, double cooldownMs, Action<string>? debug)
    {
        _maxAttempts = Math.Max(1, maxAttempts);
        _cooldownMs = Math.Max(0, cooldownMs);
        _debug = debug;
    }

    internal void Reset()
    {
        _pending = null;
        _resumeId = null;
        _attempts.Clear();
        _retryAt.Clear();
    }

    internal List<GoalCandidate> Filter(IReadOnlyList<GoalCandidate> candidates, double now) =>
        candidates.Where(candidate =>
            !_retryAt.TryGetValue(candidate.Id, out var retryAt) || retryAt <= now).ToList();

    internal GoalCandidate? Select(
        IReadOnlyList<GoalCandidate> candidates,
        double now,
        Func<string, UtilityDescription?> description,
        Func<IReadOnlyList<GoalCandidate>, GoalCandidate> pick)
    {
        var pool = Filter(candidates, now);
        if (pool.Count == 0)
        {
            return null;
        }

        if (_resumeId != null)
        {
            var resumedId = _resumeId;
            _resumeId = null;
            var resumed = pool.FirstOrDefault(candidate => candidate.Id == resumedId);
            if (resumed.Id != null)
            {
                var missing = UtilityMath.Requirements(description(resumed.Id));
                if (missing.Count == 0)
                {
                    ClearAttempts(resumed.Id);
                    _debug?.Invoke($"[prereq] resume {resumed.Id}");
                    return resumed;
                }

                var provider = Provider(pool, resumed.Id, missing, description);
                if (provider != null && RecordAttempt(resumed.Id, missing))
                {
                    _debug?.Invoke($"[prereq] {resumed.Id} needs {string.Join(",", missing)} -> {provider.Value.Id}");
                    _pending = (resumed.Id, provider.Value.Id);
                    return provider;
                }

                _debug?.Invoke($"[prereq] {resumed.Id} needs {string.Join(",", missing)} exhausted");
                _retryAt[resumed.Id] = now + _cooldownMs;
                pool = pool.Where(candidate => candidate.Id != resumed.Id).ToList();
            }
        }

        while (pool.Count > 0)
        {
            var chosen = pick(pool);
            var missing = UtilityMath.Requirements(description(chosen.Id));
            if (missing.Count == 0)
            {
                ClearAttempts(chosen.Id);
                return chosen;
            }

            var provider = Provider(pool, chosen.Id, missing, description);
            if (provider != null && RecordAttempt(chosen.Id, missing))
            {
                _debug?.Invoke($"[prereq] {chosen.Id} needs {string.Join(",", missing)} -> {provider.Value.Id}");
                _pending = (chosen.Id, provider.Value.Id);
                return provider;
            }

            _debug?.Invoke($"[prereq] {chosen.Id} needs {string.Join(",", missing)} exhausted");
            _retryAt[chosen.Id] = now + _cooldownMs;
            pool = pool.Where(candidate => candidate.Id != chosen.Id).ToList();
        }

        return null;
    }

    internal void Settled(string goalId, string reason, double now)
    {
        if (_pending is { } pending && pending.ProviderId == goalId)
        {
            _pending = null;
            if (reason == "success")
            {
                _resumeId = pending.GoalId;
                _debug?.Invoke($"[prereq] provider {goalId} solved {pending.GoalId}");
            }
            else
            {
                _retryAt[pending.GoalId] = now + _cooldownMs;
                _debug?.Invoke($"[prereq] provider {goalId} failed; retry {pending.GoalId} later");
            }
        }

        if (reason == "success")
        {
            ClearAttempts(goalId);
            _retryAt.Remove(goalId);
        }
    }

    private GoalCandidate? Provider(
        IReadOnlyList<GoalCandidate> candidates,
        string goalId,
        IReadOnlyList<string> needs,
        Func<string, UtilityDescription?> description)
    {
        GoalCandidate? best = null;
        foreach (var candidate in candidates)
        {
            if (candidate.Id == goalId)
            {
                continue;
            }

            var provider = description(candidate.Id);
            if (provider == null || !needs.Any(need => provider.Provides.Contains(need)))
            {
                continue;
            }

            if (provider.Requires.Count > 0)
            {
                continue;
            }

            if (best == null || candidate.Score > best.Value.Score)
            {
                best = candidate;
            }
        }

        return best;
    }

    private bool RecordAttempt(string goalId, IReadOnlyList<string> missing)
    {
        var key = UtilityMath.AttemptKey(goalId, missing);
        _attempts.TryGetValue(key, out var attempts);
        attempts++;
        _attempts[key] = attempts;
        return attempts <= _maxAttempts;
    }

    private void ClearAttempts(string goalId)
    {
        var prefix = goalId + "\0";
        foreach (var key in _attempts.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            _attempts.Remove(key);
        }
    }
}
