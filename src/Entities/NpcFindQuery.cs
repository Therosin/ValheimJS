using System;
using System.Collections.Generic;
using System.Linq;

namespace ValheimJS.Entities;

/// <summary>
/// Declarative query over the NPC perception store. The same shape backs
/// <c>npc.findObject</c>, <c>npc.findObjects</c>, and the native
/// <c>findObject</c>/<c>findObjects</c> behaviour actions.
/// </summary>
internal sealed class NpcFindQuery
{
    internal string? Kind { get; set; }
    internal string? Prefab { get; set; }
    internal string? Name { get; set; }
    internal List<string> TagsAny { get; } = new();
    internal List<string> TagsAll { get; } = new();
    internal List<string> VerbsAny { get; } = new();
    internal List<string> VerbsAll { get; } = new();
    internal List<string> RequiresAny { get; } = new();
    internal List<string> RequiresAll { get; } = new();
    internal bool? Ready { get; set; }
    internal bool? Usable { get; set; }
    internal bool? Visible { get; set; }
    internal bool? InRange { get; set; }
    internal bool? Enemy { get; set; }
    internal bool? IsPlayer { get; set; }
    internal bool? IsNpc { get; set; }
    internal bool? IsTamed { get; set; }
    internal bool? IsDead { get; set; }
    internal bool? IsObject { get; set; }
    internal bool? Food { get; set; }
    internal float Radius { get; set; } = -1f;
    internal float MinDistance { get; set; } = -1f;
    internal int Max { get; set; } = 32;
    internal int Skip { get; set; }
    internal bool IncludeRemembered { get; set; }
    internal HashSet<string> Exclude { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal string Sort { get; set; } = "distance";
    internal bool IgnoreRange { get; set; }
    private readonly List<NpcFindExpression> _groups = new();

    internal bool Allows(NpcSensedEntity entry, double originX, double originZ)
    {
        if (!IgnoreRange && !IncludeRemembered && !entry.InRange)
        {
            return false;
        }

        if (Kind != null && !MatchesKind(entry, Kind))
        {
            return false;
        }

        if (Prefab != null && !EqualsIgnoreCase(entry.Prefab, Prefab))
        {
            return false;
        }

        if (Name != null && !EqualsIgnoreCase(entry.Name, Name))
        {
            return false;
        }

        if (TagsAny.Count > 0 && !TagsAny.Any(tag => Has(entry.Tags, tag)))
        {
            return false;
        }

        if (TagsAll.Count > 0 && !TagsAll.All(tag => Has(entry.Tags, tag)))
        {
            return false;
        }

        if (VerbsAny.Count > 0 && !VerbsAny.Any(verb => Has(entry.Verbs, verb)))
        {
            return false;
        }

        if (VerbsAll.Count > 0 && !VerbsAll.All(verb => Has(entry.Verbs, verb)))
        {
            return false;
        }

        if (RequiresAny.Count > 0 && !RequiresAny.Any(requirement => Has(entry.Requires, requirement)))
        {
            return false;
        }

        if (RequiresAll.Count > 0 && !RequiresAll.All(requirement => Has(entry.Requires, requirement)))
        {
            return false;
        }

        if (Ready.HasValue && entry.Ready != Ready.Value) return false;
        if (Usable.HasValue && entry.Usable != Usable.Value) return false;
        if (Visible.HasValue && entry.Visible != Visible.Value) return false;
        if (InRange.HasValue && entry.InRange != InRange.Value) return false;
        if (Enemy.HasValue && entry.Enemy != Enemy.Value) return false;
        if (IsPlayer.HasValue && entry.IsPlayer != IsPlayer.Value) return false;
        if (IsNpc.HasValue && entry.IsNpc != IsNpc.Value) return false;
        if (IsTamed.HasValue && entry.IsTamed != IsTamed.Value) return false;
        if (IsDead.HasValue && entry.IsDead != IsDead.Value) return false;
        if (IsObject.HasValue && entry.IsObject != IsObject.Value) return false;
        if (Food.HasValue && entry.Food != Food.Value) return false;

        if (Exclude.Contains(entry.Id))
        {
            return false;
        }

        var dx = entry.X - originX;
        var dz = entry.Z - originZ;
        var distance = Math.Sqrt(dx * dx + dz * dz);
        if (Radius >= 0f && distance > Radius)
        {
            return false;
        }

        if (MinDistance >= 0f && distance < MinDistance)
        {
            return false;
        }

        foreach (var group in _groups)
        {
            if (!group.Evaluate(entry, originX, originZ))
            {
                return false;
            }
        }

        return true;
    }

    internal static NpcFindQuery FromPlain(IReadOnlyDictionary<string, object?>? options, string? defaultKind = null)
    {
        var query = new NpcFindQuery { Kind = defaultKind };
        if (options == null)
        {
            return query;
        }

        foreach (var pair in options)
        {
            switch (pair.Key)
            {
                case "kind": query.Kind = String(pair.Value, pair.Key); break;
                case "prefab": query.Prefab = String(pair.Value, pair.Key); break;
                case "name": query.Name = String(pair.Value, pair.Key); break;
                case "tags": ApplySet(query.TagsAny, query.TagsAll, pair.Value, pair.Key); break;
                case "verbs": ApplySet(query.VerbsAny, query.VerbsAll, pair.Value, pair.Key); break;
                case "requires": ApplySet(query.RequiresAny, query.RequiresAll, pair.Value, pair.Key); break;
                case "where": ApplyWhere(query, pair.Value); break;
                case "orderBy": query.Sort = OrderBy(pair.Value); break;
                case "take": query.Max = Take(pair.Value, pair.Key); break;
                case "max": query.Max = Take(pair.Value, pair.Key); break;
                case "skip": query.Skip = ParseSkip(pair.Value, pair.Key); break;
                case "radius": query.Radius = Number(pair.Value, pair.Key); break;
                case "includeRemembered": query.IncludeRemembered = Bool(pair.Value, pair.Key); break;
                case "exclude": ParseExclude(query, pair.Value, pair.Key); break;
                default:
                    throw new ArgumentException($"find: unknown query field '{pair.Key}'.");
            }
        }

        return query;
    }

    private static void ApplyWhere(NpcFindQuery query, object? value)
    {
        if (value is not IReadOnlyDictionary<string, object?> where)
        {
            throw new ArgumentException("find: 'where' must be an object.");
        }

        foreach (var pair in where)
        {
            switch (pair.Key)
            {
                case "kind": query.Kind = String(pair.Value, "where.kind"); break;
                case "prefab": query.Prefab = String(pair.Value, "where.prefab"); break;
                case "name": query.Name = String(pair.Value, "where.name"); break;
                case "tags": ApplySet(query.TagsAny, query.TagsAll, pair.Value, "where.tags"); break;
                case "verbs": ApplySet(query.VerbsAny, query.VerbsAll, pair.Value, "where.verbs"); break;
                case "requires": ApplySet(query.RequiresAny, query.RequiresAll, pair.Value, "where.requires"); break;
                case "ready": query.Ready = Bool(pair.Value, "where.ready"); break;
                case "usable": query.Usable = Bool(pair.Value, "where.usable"); break;
                case "visible": query.Visible = Bool(pair.Value, "where.visible"); break;
                case "inRange": query.InRange = Bool(pair.Value, "where.inRange"); break;
                case "enemy": query.Enemy = Bool(pair.Value, "where.enemy"); break;
                case "isPlayer": query.IsPlayer = Bool(pair.Value, "where.isPlayer"); break;
                case "isNpc": query.IsNpc = Bool(pair.Value, "where.isNpc"); break;
                case "isTamed": query.IsTamed = Bool(pair.Value, "where.isTamed"); break;
                case "isDead": query.IsDead = Bool(pair.Value, "where.isDead"); break;
                case "isObject": query.IsObject = Bool(pair.Value, "where.isObject"); break;
                case "food": query.Food = Bool(pair.Value, "where.food"); break;
                case "distance": ApplyRange(query, pair.Value, "where.distance"); break;
                case "and":
                    query._groups.Add(new AndExpression(ParseList(pair.Value, "and")));
                    break;
                case "or":
                    query._groups.Add(new OrExpression(ParseList(pair.Value, "or")));
                    break;
                case "not":
                    query._groups.Add(new NotExpression(ParseExpression(pair.Value)));
                    break;
                default:
                    throw new ArgumentException($"find: unknown where field '{pair.Key}'.");
            }
        }
    }

    private static void ApplyRange(NpcFindQuery query, object? value, string label)
    {
        if (value is not IReadOnlyDictionary<string, object?> range)
        {
            query.Radius = Number(value, label);
            return;
        }

        foreach (var pair in range)
        {
            var number = Number(pair.Value, label + "." + pair.Key);
            switch (pair.Key)
            {
                case "lte":
                case "max":
                    query.Radius = number;
                    break;
                case "gte":
                case "min":
                    query.MinDistance = number;
                    break;
                case "eq":
                    query.MinDistance = number;
                    query.Radius = number;
                    break;
                default:
                    throw new ArgumentException($"find: unknown distance operator '{pair.Key}'.");
            }
        }
    }

    private static void ApplySet(List<string> any, List<string> all, object? value, string label)
    {
        if (value is string text)
        {
            any.Add(text);
            return;
        }

        if (value is IReadOnlyList<object?> list)
        {
            foreach (var item in list)
            {
                any.Add(String(item, label));
            }

            return;
        }

        if (value is IReadOnlyDictionary<string, object?> map)
        {
            foreach (var pair in map)
            {
                switch (pair.Key)
                {
                    case "anyOf":
                        foreach (var item in List(pair.Value, label + ".anyOf"))
                        {
                            any.Add(String(item, label + ".anyOf"));
                        }
                        break;
                    case "allOf":
                        foreach (var item in List(pair.Value, label + ".allOf"))
                        {
                            all.Add(String(item, label + ".allOf"));
                        }
                        break;
                    default:
                        throw new ArgumentException($"find: unknown set operator '{label}.{pair.Key}'.");
                }
            }

            return;
        }

        throw new ArgumentException($"find: '{label}' must be a string, array or {{anyOf/allOf}} object.");
    }

    private static void ParseExclude(NpcFindQuery query, object? value, string label)
    {
        foreach (var item in List(value, label))
        {
            query.Exclude.Add(String(item, label));
        }
    }

    private static string OrderBy(object? value)
    {
        var text = value is IReadOnlyList<object?> list
            ? list.Count > 0 ? String(list[0], "orderBy") : "distance"
            : String(value, "orderBy");
        return text switch
        {
            "distance" or "health" or "level" or "seenCount" => text,
            _ => throw new ArgumentException($"find: unknown orderBy '{text}'.")
        };
    }

    private static int Take(object? value, string label)
    {
        var number = (int)Number(value, label);
        return Math.Clamp(number, 1, 1024);
    }

    private static int ParseSkip(object? value, string label) =>
        Math.Max(0, (int)Number(value, label));

    private static string String(object? value, string label) =>
        value as string ?? throw new ArgumentException($"find: '{label}' must be a string.");

    private static bool Bool(object? value, string label) =>
        value is bool flag ? flag : throw new ArgumentException($"find: '{label}' must be a boolean.");

    private static float Number(object? value, string label) => value switch
    {
        long integer => integer,
        int integer => integer,
        double number when !double.IsNaN(number) && !double.IsInfinity(number) => (float)number,
        float number when !float.IsNaN(number) && !float.IsInfinity(number) => number,
        _ => throw new ArgumentException($"find: '{label}' must be a finite number.")
    };

    private static IReadOnlyList<object?> List(object? value, string label) =>
        value as IReadOnlyList<object?> ?? throw new ArgumentException($"find: '{label}' must be an array.");

    private static NpcFindExpression ParseExpression(object? value)
    {
        if (value is not IReadOnlyDictionary<string, object?> map)
        {
            throw new ArgumentException("find: logical groups must be objects.");
        }

        var expressions = new List<NpcFindExpression>();
        var leaf = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in map)
        {
            switch (pair.Key)
            {
                case "and":
                    expressions.Add(new AndExpression(ParseList(pair.Value, "and")));
                    break;
                case "or":
                    expressions.Add(new OrExpression(ParseList(pair.Value, "or")));
                    break;
                case "not":
                    expressions.Add(new NotExpression(ParseExpression(pair.Value)));
                    break;
                default:
                    leaf[pair.Key] = pair.Value;
                    break;
            }
        }

        if (leaf.Count > 0)
        {
            var query = FromPlain(leaf);
            query.IgnoreRange = true;
            expressions.Add(new LeafExpression(query));
        }

        if (expressions.Count == 0)
        {
            throw new ArgumentException("find: logical group cannot be empty.");
        }

        return expressions.Count == 1
            ? expressions[0]
            : new AndExpression(expressions);
    }

    private static IReadOnlyList<NpcFindExpression> ParseList(object? value, string label)
    {
        if (value is IReadOnlyDictionary<string, object?> map)
        {
            return new[] { ParseExpression(map) };
        }

        if (value is not IReadOnlyList<object?> list || list.Count == 0)
        {
            throw new ArgumentException($"find: '{label}' must be a non-empty array of query objects.");
        }

        var expressions = new List<NpcFindExpression>(list.Count);
        foreach (var item in list)
        {
            expressions.Add(ParseExpression(item));
        }

        return expressions;
    }

    private abstract class NpcFindExpression
    {
        internal abstract bool Evaluate(NpcSensedEntity entry, double originX, double originZ);
    }

    private sealed class LeafExpression : NpcFindExpression
    {
        private readonly NpcFindQuery _query;

        internal LeafExpression(NpcFindQuery query) => _query = query;

        internal override bool Evaluate(NpcSensedEntity entry, double originX, double originZ) =>
            _query.Allows(entry, originX, originZ);
    }

    private sealed class AndExpression : NpcFindExpression
    {
        private readonly IReadOnlyList<NpcFindExpression> _expressions;

        internal AndExpression(IReadOnlyList<NpcFindExpression> expressions) => _expressions = expressions;

        internal override bool Evaluate(NpcSensedEntity entry, double originX, double originZ)
        {
            foreach (var expression in _expressions)
            {
                if (!expression.Evaluate(entry, originX, originZ))
                {
                    return false;
                }
            }

            return true;
        }
    }

    private sealed class OrExpression : NpcFindExpression
    {
        private readonly IReadOnlyList<NpcFindExpression> _expressions;

        internal OrExpression(IReadOnlyList<NpcFindExpression> expressions) => _expressions = expressions;

        internal override bool Evaluate(NpcSensedEntity entry, double originX, double originZ)
        {
            foreach (var expression in _expressions)
            {
                if (expression.Evaluate(entry, originX, originZ))
                {
                    return true;
                }
            }

            return false;
        }
    }

    private sealed class NotExpression : NpcFindExpression
    {
        private readonly NpcFindExpression _expression;

        internal NotExpression(NpcFindExpression expression) => _expression = expression;

        internal override bool Evaluate(NpcSensedEntity entry, double originX, double originZ) =>
            !_expression.Evaluate(entry, originX, originZ);
    }

    private static bool MatchesKind(NpcSensedEntity entry, string kind)
    {
        if (EqualsIgnoreCase(entry.Kind, kind) || EqualsIgnoreCase(entry.Prefab, kind) || Has(entry.Tags, kind))
        {
            return true;
        }

        return kind.ToLowerInvariant() switch
        {
            "player" => entry.IsPlayer,
            "npc" => entry.IsNpc,
            "creature" => !entry.IsPlayer && !entry.IsNpc,
            "tamed" => entry.IsTamed,
            "enemy" => entry.Enemy,
            "dead" => entry.IsDead,
            "object" => entry.IsObject,
            _ => false
        };
    }

    private static bool Has(IReadOnlyCollection<string> values, string expected)
    {
        foreach (var value in values)
        {
            if (EqualsIgnoreCase(value, expected))
            {
                return true;
            }
        }

        return false;
    }

    private static bool EqualsIgnoreCase(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
