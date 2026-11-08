using System;
using System.Collections.Generic;
using System.IO;
using ValheimJS.Utils;

namespace ValheimJS.Core;

/// <summary>Outcome of one node or one instance tick.</summary>
public enum BehaviourStatus
{
    /// <summary>The node finished its job.</summary>
    Success,

    /// <summary>The node could not finish its job.</summary>
    Failure,

    /// <summary>The node is still working and expects further ticks.</summary>
    Running,

    /// <summary>The instance was cancelled; only <see cref="BehaviourInstance.Tick"/> reports this.</summary>
    Cancelled
}

/// <summary>Node shapes supported by the engine.</summary>
public enum BehaviourNodeKind
{
    Sequence,
    Selector,
    Random,
    Action,
    Condition,

    /// <summary>
    /// Transfers control to a named node in the same file. Imports keep their own id scope, so a
    /// goto inside an imported tree cannot target the importing file and vice versa.
    /// </summary>
    Goto,

    Use
}

/// <summary>Result returned by a host action or by a tick.</summary>
public readonly struct BehaviourResult
{
    public BehaviourResult(BehaviourStatus status, object? output = null)
    {
        Status = status;
        Output = output;
    }

    public BehaviourStatus Status { get; }

    /// <summary>Value assigned to the node's <c>out</c> reference when the result is a success.</summary>
    public object? Output { get; }

    public bool IsTerminal => Status == BehaviourStatus.Success || Status == BehaviourStatus.Failure;

    public static BehaviourResult Success(object? output = null) => new(BehaviourStatus.Success, output);

    public static BehaviourResult Failure(object? output = null) => new(BehaviourStatus.Failure, output);

    public static BehaviourResult Running => new(BehaviourStatus.Running);
}

/// <summary>Per-call view passed to host callbacks and inline scripts.</summary>
public sealed class BehaviourContext
{
    internal BehaviourContext(
        IReadOnlyDictionary<string, object?> args,
        IDictionary<string, object?> state,
        object? host,
        float deltaTime,
        bool interrupted)
    {
        Args = args;
        State = state;
        This = host;
        DeltaTime = deltaTime;
        Interrupted = interrupted;
    }

    /// <summary>The current node's arguments, with <c>@[state]</c> references already resolved.</summary>
    public IReadOnlyDictionary<string, object?> Args { get; }

    /// <summary>Mutable instance-wide state shared by every node, import and action.</summary>
    public IDictionary<string, object?> State { get; }

    /// <summary>The opaque object supplied when the instance was created.</summary>
    public object? This { get; }

    /// <summary>Delta time passed to the tick that is currently executing.</summary>
    public float DeltaTime { get; }

    /// <summary>True when this context belongs to an <c>execAfter</c> run caused by cancellation, reset or a jump.</summary>
    public bool Interrupted { get; }
}

/// <summary>Runs an inline <c>exec*</c> script. The host owns the interpreter.</summary>
public delegate object? BehaviourScriptExecutor(string code, BehaviourContext context);

/// <summary>Resolves an <c>action</c> name. Return Running to keep the node active across ticks.</summary>
public delegate BehaviourResult BehaviourActionExecutor(string action, BehaviourContext context);

/// <summary>Resolves a <c>condition</c> name.</summary>
public delegate bool BehaviourConditionEvaluator(string condition, BehaviourContext context);

/// <summary>Load or execution error. Host callback exceptions are not wrapped; they propagate as-is.</summary>
public sealed class BehaviourException : Exception
{
    public BehaviourException(string message)
        : base(message)
    {
    }

    public BehaviourException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>One node of an immutable parsed definition. Runtime position lives in the instance.</summary>
public sealed class BehaviourNode
{
    private static readonly Dictionary<string, object?> NoArguments = new(0);

    internal BehaviourNode(BehaviourNodeKind kind) => Kind = kind;

    public string? Id { get; internal set; }

    public BehaviourNodeKind Kind { get; }

    public IReadOnlyList<BehaviourNode> Children { get; internal set; } = Array.Empty<BehaviourNode>();

    public IReadOnlyDictionary<string, object?> Arguments { get; internal set; } = NoArguments;

    /// <summary>Host action name for <see cref="BehaviourNodeKind.Action"/>.</summary>
    public string? Action { get; internal set; }

    /// <summary>Host condition name for <see cref="BehaviourNodeKind.Condition"/>.</summary>
    public string? Condition { get; internal set; }

    public BehaviourNode? WhenTrue { get; internal set; }

    public BehaviourNode? WhenFalse { get; internal set; }

    /// <summary>Selection weight when this node is a child of a random node. Defaults to 1.</summary>
    public double Weight { get; internal set; } = 1;

    public string? ExecBefore { get; internal set; }

    public string? Exec { get; internal set; }

    public string? ExecAfter { get; internal set; }

    /// <summary>State path assigned from the action output on success.</summary>
    public IReadOnlyList<string>? Output { get; internal set; }

    /// <summary>Imported definition executed by a <see cref="BehaviourNodeKind.Use"/> node.</summary>
    public BehaviourTree? Use { get; internal set; }

    internal string? GotoId { get; set; }

    internal BehaviourNode? JumpTarget { get; set; }

    internal BehaviourNode? Parent { get; set; }

    internal int ParentIndex { get; set; }

    internal string Describe() => Id != null ? $"'{Id}' ({Kind})" : Kind.ToString();
}

/// <summary>
/// Parsed YAML behaviour definition. Immutable: create one tree and any number of independent
/// <see cref="BehaviourInstance"/> objects from it.
/// </summary>
public sealed class BehaviourTree
{
    private static readonly HashSet<string> TopLevelKeys = new(StringComparer.Ordinal)
    {
        "id", "description", "imports", "state", "root"
    };

    private static readonly HashSet<string> NodeKeys = new(StringComparer.Ordinal)
    {
        "id", "description", "type", "children", "action", "condition", "true", "false",
        "goto", "use", "args", "out", "weight", "execBefore", "exec", "execAfter"
    };

    private BehaviourTree(
        string id,
        string? description,
        string sourcePath,
        BehaviourNode root,
        Dictionary<string, object?> initialState,
        Dictionary<string, BehaviourTree> imports)
    {
        Id = id;
        Description = description;
        SourcePath = sourcePath;
        Root = root;
        InitialState = initialState;
        Imports = imports;
    }

    public string Id { get; }

    public string? Description { get; }

    /// <summary>Absolute path for loaded trees, or the caller-supplied name for parsed text.</summary>
    public string SourcePath { get; }

    public BehaviourNode Root { get; }

    /// <summary>Defaults merged from imports (declaration order) and this file's own <c>state</c> block.</summary>
    public IReadOnlyDictionary<string, object?> InitialState { get; }

    public IReadOnlyDictionary<string, BehaviourTree> Imports { get; }

    /// <summary>Loads a file and resolves its imports relative to that file.</summary>
    public static BehaviourTree Load(string path, Action<string>? warn = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A behaviour file path is required.", nameof(path));
        }

        return new Loader(warn).LoadFile(Path.GetFullPath(path), importer: null);
    }

    /// <summary>Parses YAML text. <paramref name="sourcePath"/> names the source and anchors relative imports.</summary>
    /// <param name="importResolver">
    /// Optional in-memory import source keyed by the alias declared in <c>imports:</c>. When supplied,
    /// imports are read from it instead of disk; a missing alias is an error. Aliases are global to
    /// one load, so nested trees share the same resolver.
    /// </param>
    public static BehaviourTree Parse(
        string yaml,
        string? sourcePath = null,
        Action<string>? warn = null,
        Func<string, string?>? importResolver = null)
    {
        if (yaml == null)
        {
            throw new ArgumentNullException(nameof(yaml));
        }

        return new Loader(warn, importResolver).Build(yaml, NormalizeSource(sourcePath));
    }

    /// <summary>Creates independent execution state for this definition.</summary>
    public BehaviourInstance CreateInstance(
        object? host = null,
        BehaviourScriptExecutor? execute = null,
        BehaviourActionExecutor? action = null,
        BehaviourConditionEvaluator? condition = null,
        Func<double>? random = null) =>
        new(this, host, execute, action, condition, random);

    internal static object? Clone(object? value) => value switch
    {
        Dictionary<string, object?> map => CloneMap(map),
        List<object?> list => CloneList(list),
        _ => value
    };

    internal static Dictionary<string, object?> ToPlainMap(IReadOnlyDictionary<string, object?> map)
    {
        var result = new Dictionary<string, object?>(map.Count, StringComparer.Ordinal);
        foreach (var pair in map)
        {
            result[pair.Key] = ToPlainValue(pair.Value);
        }

        return result;
    }

    internal static object? ToPlainValue(object? value) => value switch
    {
        YamlMap map => ToPlainMap(map),
        List<object?> list => ToPlainList(list),
        _ => value
    };

    private static Dictionary<string, object?> CloneMap(Dictionary<string, object?> map)
    {
        var result = new Dictionary<string, object?>(map.Count, StringComparer.Ordinal);
        foreach (var pair in map)
        {
            result[pair.Key] = Clone(pair.Value);
        }

        return result;
    }

    private static List<object?> CloneList(List<object?> list)
    {
        var result = new List<object?>(list.Count);
        foreach (var item in list)
        {
            result.Add(Clone(item));
        }

        return result;
    }

    private static List<object?> ToPlainList(List<object?> list)
    {
        var result = new List<object?>(list.Count);
        foreach (var item in list)
        {
            result.Add(ToPlainValue(item));
        }

        return result;
    }

    private sealed class NodeContext
    {
        internal string SourcePath = string.Empty;
        internal IReadOnlyDictionary<string, BehaviourTree> Imports = null!;
        internal Dictionary<string, BehaviourNode> Ids = new(StringComparer.Ordinal);
        internal List<BehaviourNode> Gotos = new();
    }

    internal static bool TryParseReference(string text, out List<string> segments) =>
        Loader.TryParseReference(text, out segments);

    private static string NormalizeSource(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(sourcePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return sourcePath;
        }
    }

    private sealed class Loader
    {
        private readonly Action<string>? _warn;
        private readonly Func<string, string?>? _inlineImports;
        private readonly Dictionary<string, BehaviourTree> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, BehaviourTree> _inlineCache = new(StringComparer.Ordinal);
        private readonly List<string> _stack = new();
        private readonly List<string> _inlineStack = new();

        internal Loader(Action<string>? warn, Func<string, string?>? inlineImports = null)
        {
            _warn = warn;
            _inlineImports = inlineImports;
        }

        internal BehaviourTree LoadFile(string fullPath, string? importer)
        {
            if (_cache.TryGetValue(fullPath, out var cached))
            {
                return cached;
            }

            foreach (var loading in _stack)
            {
                if (string.Equals(loading, fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new BehaviourException(
                        $"Circular import: '{fullPath}' is already being loaded ({string.Join(" -> ", _stack)} -> {fullPath}).");
                }
            }

            if (!File.Exists(fullPath))
            {
                throw new BehaviourException(importer == null
                    ? $"Behaviour file not found: '{fullPath}'."
                    : $"Import '{fullPath}' was not found (imported by '{importer}').");
            }

            string text;
            try
            {
                text = File.ReadAllText(fullPath);
            }
            catch (Exception exception)
            {
                throw new BehaviourException($"Could not read behaviour file '{fullPath}': {exception.Message}", exception);
            }

            _stack.Add(fullPath);
            try
            {
                var tree = Build(text, fullPath);
                _cache[fullPath] = tree;
                return tree;
            }
            finally
            {
                _stack.RemoveAt(_stack.Count - 1);
            }
        }

        private BehaviourTree ResolveInline(string alias, string relative, string importer)
        {
            if (_inlineCache.TryGetValue(alias, out var cached))
            {
                return cached;
            }

            foreach (var active in _inlineStack)
            {
                if (string.Equals(active, alias, StringComparison.Ordinal))
                {
                    throw new BehaviourException(
                        $"Circular import: '{alias}' is already being loaded ({string.Join(" -> ", _inlineStack)} -> {alias}).");
                }
            }

            var text = _inlineImports!(alias);
            if (text == null)
            {
                throw new BehaviourException(
                    $"Import '{alias}' ({relative}) was not supplied (imported by '{Describe(importer)}').");
            }

            _inlineStack.Add(alias);
            try
            {
                var tree = Build(text, alias);
                _inlineCache[alias] = tree;
                return tree;
            }
            finally
            {
                _inlineStack.RemoveAt(_inlineStack.Count - 1);
            }
        }

        internal BehaviourTree Build(string text, string sourcePath)
        {
            object? document;
            try
            {
                document = Yaml.Parse(text, sourcePath.Length == 0 ? null : sourcePath);
            }
            catch (YamlException exception)
            {
                throw new BehaviourException(exception.Message, exception);
            }

            if (document is not YamlMap map)
            {
                throw new BehaviourException($"{Describe(sourcePath)}: the document root must be a mapping.");
            }

            ValidateKeys(map, TopLevelKeys, sourcePath, "the document");

            var id = OptionalString(map, "id", sourcePath, "the document") ?? string.Empty;
            var description = OptionalString(map, "description", sourcePath, "the document");

            var imports = new Dictionary<string, BehaviourTree>(StringComparer.Ordinal);
            if (map.TryGetValue("imports", out var importsValue) && importsValue != null)
            {
                if (importsValue is not YamlMap importMap)
                {
                    throw new BehaviourException($"{Describe(sourcePath)}: 'imports' must be a mapping of alias to file path.");
                }

                var baseDirectory = BaseDirectory(sourcePath);
                foreach (var pair in importMap)
                {
                    if (pair.Key.Trim().Length == 0)
                    {
                        throw new BehaviourException($"{Describe(sourcePath)}: an import alias cannot be blank.");
                    }

                    if (pair.Value is not string relative || relative.Trim().Length == 0)
                    {
                        throw new BehaviourException($"{Describe(sourcePath)}: import '{pair.Key}' must be a file path.");
                    }

                    imports.Add(pair.Key, _inlineImports != null
                        ? ResolveInline(pair.Key, relative, sourcePath)
                        : LoadFile(Path.GetFullPath(Path.Combine(baseDirectory, relative)), sourcePath));
                }
            }

            var initialState = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var import in imports)
            {
                MergeState(initialState, import.Value.InitialState, import.Key, sourcePath, _warn);
            }

            if (map.TryGetValue("state", out var stateValue) && stateValue != null)
            {
                if (stateValue is not YamlMap stateMap)
                {
                    throw new BehaviourException($"{Describe(sourcePath)}: 'state' must be a mapping.");
                }

                MergeState(initialState, ToPlainMap(stateMap), null, sourcePath, null);
            }

            if (!map.TryGetValue("root", out var rootValue) || rootValue == null)
            {
                throw new BehaviourException($"{Describe(sourcePath)}: missing 'root' node.");
            }

            var context = new NodeContext { SourcePath = sourcePath, Imports = imports };
            var root = ParseNode(rootValue, $"{Describe(sourcePath)} root", null, context);

            foreach (var node in context.Gotos)
            {
                if (!context.Ids.TryGetValue(node.GotoId!, out var target))
                {
                    throw new BehaviourException(
                        $"{Describe(sourcePath)}: goto target '{node.GotoId}' is not declared in this file.");
                }

                node.JumpTarget = target;
            }

            return new BehaviourTree(id, description, sourcePath, root, initialState, imports);
        }

        private BehaviourNode ParseNode(object? value, string location, BehaviourNodeKind? parentKind, NodeContext context)
        {
            if (value is not YamlMap map)
            {
                throw new BehaviourException($"{location}: a node must be a mapping, got {DescribeValue(value)}.");
            }

            ValidateKeys(map, NodeKeys, context.SourcePath, location);

            var hasType = map.TryGetValue("type", out var typeValue);
            var hasAction = map.TryGetValue("action", out var actionValue);
            var hasCondition = map.TryGetValue("condition", out var conditionValue);
            var hasGoto = map.TryGetValue("goto", out var gotoValue);
            var hasUse = map.TryGetValue("use", out var useValue);
            var selectors = (hasType ? 1 : 0) + (hasAction ? 1 : 0) + (hasCondition ? 1 : 0) +
                            (hasGoto ? 1 : 0) + (hasUse ? 1 : 0);

            if (selectors != 1)
            {
                throw new BehaviourException(
                    $"{location}: exactly one of 'type', 'action', 'condition', 'goto' or 'use' is required.");
            }

            var kind = BehaviourNodeKind.Action;
            string? actionName = null;
            string? conditionName = null;

            if (hasType)
            {
                var type = RequireString(typeValue, location, "type");
                kind = type switch
                {
                    "sequence" => BehaviourNodeKind.Sequence,
                    "selector" => BehaviourNodeKind.Selector,
                    "random" => BehaviourNodeKind.Random,
                    _ => throw new BehaviourException($"{location}: unknown node type '{type}'.")
                };
            }
            else if (hasAction)
            {
                kind = BehaviourNodeKind.Action;
                actionName = RequireString(actionValue, location, "action");
            }
            else if (hasCondition)
            {
                kind = BehaviourNodeKind.Condition;
                conditionName = RequireString(conditionValue, location, "condition");
            }
            else if (hasGoto)
            {
                kind = BehaviourNodeKind.Goto;
            }
            else
            {
                kind = BehaviourNodeKind.Use;
            }

            var weight = 1d;
            if (map.TryGetValue("weight", out var weightValue))
            {
                if (parentKind != BehaviourNodeKind.Random)
                {
                    throw new BehaviourException($"{location}: 'weight' is only valid on children of a 'random' node.");
                }

                weight = RequireNumber(weightValue, location, "weight");
                if (weight < 0 || double.IsNaN(weight) || double.IsInfinity(weight))
                {
                    throw new BehaviourException($"{location}: 'weight' must be finite and non-negative.");
                }
            }

            var node = new BehaviourNode(kind)
            {
                Action = actionName,
                Condition = conditionName,
                Weight = weight,
                ExecBefore = OptionalString(map, "execBefore", context.SourcePath, location),
                Exec = OptionalString(map, "exec", context.SourcePath, location),
                ExecAfter = OptionalString(map, "execAfter", context.SourcePath, location)
            };

            var id = OptionalString(map, "id", context.SourcePath, location);
            if (id != null)
            {
                if (context.Ids.ContainsKey(id))
                {
                    throw new BehaviourException($"{location}: duplicate node id '{id}'.");
                }

                context.Ids.Add(id, node);
                node.Id = id;
            }

            if (map.TryGetValue("args", out var argsValue) && argsValue != null)
            {
                if (argsValue is not YamlMap argsMap)
                {
                    throw new BehaviourException($"{location}: 'args' must be a mapping.");
                }

                node.Arguments = ToPlainMap(argsMap);
            }

            if (map.TryGetValue("out", out var outValue))
            {
                if (outValue is not string outText)
                {
                    throw new BehaviourException($"{location}: 'out' must be a state reference string.");
                }

                node.Output = ParseStatePath(outText, location, "out");
            }

            var children = new List<BehaviourNode>();
            if (map.TryGetValue("children", out var childrenValue))
            {
                if (kind != BehaviourNodeKind.Sequence && kind != BehaviourNodeKind.Selector && kind != BehaviourNodeKind.Random)
                {
                    throw new BehaviourException($"{location}: 'children' is only valid on sequence, selector and random nodes.");
                }

                if (childrenValue is not List<object?> items)
                {
                    throw new BehaviourException($"{location}: 'children' must be a sequence.");
                }

                if (items.Count == 0)
                {
                    throw new BehaviourException($"{location}: 'children' cannot be empty.");
                }

                for (var i = 0; i < items.Count; i++)
                {
                    children.Add(ParseNode(items[i], $"{location}.children[{i}]", kind, context));
                }

                if (kind == BehaviourNodeKind.Random)
                {
                    var positive = false;
                    foreach (var child in children)
                    {
                        positive |= child.Weight > 0;
                    }

                    if (!positive)
                    {
                        throw new BehaviourException($"{location}: a random node needs at least one child with a positive weight.");
                    }
                }
            }
            else if (kind == BehaviourNodeKind.Sequence || kind == BehaviourNodeKind.Selector || kind == BehaviourNodeKind.Random)
            {
                throw new BehaviourException($"{location}: 'children' is required on {kind.ToString().ToLowerInvariant()} nodes.");
            }

            node.Children = children;
            for (var i = 0; i < children.Count; i++)
            {
                children[i].Parent = node;
                children[i].ParentIndex = i;
            }

            if (kind == BehaviourNodeKind.Condition)
            {
                node.WhenTrue = ParseBranch(node, map, "true", 0, location, context);
                node.WhenFalse = ParseBranch(node, map, "false", 1, location, context);
            }
            else if (map.ContainsKey("true") || map.ContainsKey("false"))
            {
                throw new BehaviourException($"{location}: 'true' and 'false' branches are only valid on condition nodes.");
            }

            if (kind == BehaviourNodeKind.Goto)
            {
                node.GotoId = RequireString(gotoValue, location, "goto");
                context.Gotos.Add(node);
            }

            if (kind == BehaviourNodeKind.Use)
            {
                var alias = RequireString(useValue, location, "use");
                if (!context.Imports.TryGetValue(alias, out var imported))
                {
                    throw new BehaviourException($"{location}: 'use: {alias}' does not match a declared import.");
                }

                node.Use = imported;
            }

            return node;
        }

        private BehaviourNode? ParseBranch(
            BehaviourNode parent,
            YamlMap map,
            string key,
            int index,
            string location,
            NodeContext context)
        {
            if (!map.TryGetValue(key, out var value) || value == null)
            {
                return null;
            }

            var branch = ParseNode(value, $"{location}.{key}", BehaviourNodeKind.Condition, context);
            branch.Parent = parent;
            branch.ParentIndex = index;
            return branch;
        }

        private static IReadOnlyList<string> ParseStatePath(string text, string location, string key)
        {
            if (!TryParseReference(text, out var segments))
            {
                throw new BehaviourException(
                    $"{location}: '{key}' must be a state reference like \"@[name]\" (got '{text}').");
            }

            return segments;
        }

        internal static bool TryParseReference(string text, out List<string> segments)
        {
            segments = new List<string>();
            var trimmed = text.Trim();
            if (trimmed.Length < 4 || !trimmed.StartsWith("@[", StringComparison.Ordinal) ||
                trimmed[trimmed.Length - 1] != ']')
            {
                return false;
            }

            var inner = trimmed.Substring(2, trimmed.Length - 3).Trim();
            if (inner.Length == 0)
            {
                return false;
            }

            foreach (var part in inner.Split('.'))
            {
                var segment = part.Trim();
                if (segment.Length == 0)
                {
                    return false;
                }

                segments.Add(segment);
            }

            return true;
        }

        private static void MergeState(
            Dictionary<string, object?> target,
            IReadOnlyDictionary<string, object?> source,
            string? importAlias,
            string sourcePath,
            Action<string>? warn)
        {
            foreach (var pair in source)
            {
                if (target.TryGetValue(pair.Key, out var existing))
                {
                    if (existing is Dictionary<string, object?> targetMap &&
                        pair.Value is Dictionary<string, object?> sourceMap)
                    {
                        MergeState(targetMap, sourceMap, importAlias, sourcePath, warn);
                        continue;
                    }

                    if (importAlias != null && !ValuesEqual(existing, pair.Value))
                    {
                        warn?.Invoke(
                            $"{Describe(sourcePath)}: state '{pair.Key}' from import '{importAlias}' overrides a conflicting default.");
                    }
                }

                target[pair.Key] = Clone(pair.Value);
            }
        }

        private static bool ValuesEqual(object? left, object? right)
        {
            switch (left)
            {
                case Dictionary<string, object?> leftMap when right is Dictionary<string, object?> rightMap:
                    if (leftMap.Count != rightMap.Count)
                    {
                        return false;
                    }

                    foreach (var pair in leftMap)
                    {
                        if (!rightMap.TryGetValue(pair.Key, out var value) || !ValuesEqual(pair.Value, value))
                        {
                            return false;
                        }
                    }

                    return true;
                case List<object?> leftList when right is List<object?> rightList:
                    if (leftList.Count != rightList.Count)
                    {
                        return false;
                    }

                    for (var i = 0; i < leftList.Count; i++)
                    {
                        if (!ValuesEqual(leftList[i], rightList[i]))
                        {
                            return false;
                        }
                    }

                    return true;
                default:
                    return Equals(left, right);
            }
        }

        private static void ValidateKeys(YamlMap map, HashSet<string> allowed, string sourcePath, string location)
        {
            foreach (var pair in map)
            {
                if (!allowed.Contains(pair.Key))
                {
                    throw new BehaviourException($"{location}: unknown key '{pair.Key}' in {Describe(sourcePath)}.");
                }
            }
        }

        private static string? OptionalString(YamlMap map, string key, string sourcePath, string location)
        {
            if (!map.TryGetValue(key, out var value) || value == null)
            {
                return null;
            }

            if (value is not string text)
            {
                throw new BehaviourException($"{location}: '{key}' must be a string in {Describe(sourcePath)}.");
            }

            return text.Length == 0 ? null : text;
        }

        private static string RequireString(object? value, string location, string key)
        {
            if (value is not string text || text.Trim().Length == 0)
            {
                throw new BehaviourException($"{location}: '{key}' must be a non-empty string.");
            }

            return text.Trim();
        }

        private static double RequireNumber(object? value, string location, string key)
        {
            switch (value)
            {
                case long integer:
                    return integer;
                case double number:
                    return number;
                default:
                    throw new BehaviourException($"{location}: '{key}' must be a number.");
            }
        }

        private static string DescribeValue(object? value) => value switch
        {
            null => "null",
            string => "a string",
            long or double => "a number",
            bool => "a boolean",
            List<object?> => "a sequence",
            YamlMap => "a mapping",
            _ => value.GetType().Name
        };

        private static string Describe(string sourcePath) =>
            sourcePath.Length == 0 ? "<behaviour>" : sourcePath;

        private static string BaseDirectory(string sourcePath) =>
            sourcePath.Length == 0 ? Directory.GetCurrentDirectory() : Path.GetDirectoryName(sourcePath) ?? Directory.GetCurrentDirectory();
    }
}

/// <summary>
/// Stateful execution of a <see cref="BehaviourTree"/>. Call <see cref="Tick"/> at whatever cadence
/// the host chooses; a tick returns as soon as the tree is Running, Succeeds or Fails.
/// </summary>
public sealed class BehaviourInstance
{
    private static readonly Dictionary<string, object?> NoArguments = new(0);
    private static readonly Random SharedRandom = new();

    private readonly BehaviourTree _tree;
    private readonly object? _host;
    private readonly BehaviourScriptExecutor? _execute;
    private readonly BehaviourActionExecutor? _action;
    private readonly BehaviourConditionEvaluator? _condition;
    private readonly Func<double> _random;
    private readonly Dictionary<string, object?> _state;
    private readonly Dictionary<BehaviourNode, Frame> _frames = new();
    private readonly Frame _mainFrame = new();
    private int _maxJumpsPerTick = 64;
    private bool _cancelled;

    internal BehaviourInstance(
        BehaviourTree tree,
        object? host,
        BehaviourScriptExecutor? execute,
        BehaviourActionExecutor? action,
        BehaviourConditionEvaluator? condition,
        Func<double>? random)
    {
        _tree = tree;
        _host = host;
        _execute = execute;
        _action = action;
        _condition = condition;
        _random = random ?? NextSharedRandom;

        _state = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in tree.InitialState)
        {
            _state[pair.Key] = BehaviourTree.Clone(pair.Value);
        }
    }

    public BehaviourTree Tree => _tree;

    /// <summary>Live mutable state. Host callbacks and scripts read and write the same dictionary.</summary>
    public IDictionary<string, object?> State => _state;

    /// <summary>Status of the most recent tick.</summary>
    public BehaviourStatus Status { get; private set; } = BehaviourStatus.Running;

    /// <summary>Delta time supplied to the most recent tick.</summary>
    public float DeltaTime { get; private set; }

    public bool IsCancelled => _cancelled;

    /// <summary>Maximum jumps a single tick may take before the engine reports a runaway loop.</summary>
    public int MaxJumpsPerTick
    {
        get => _maxJumpsPerTick;
        set
        {
            if (value < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "The jump budget must be at least 1.");
            }

            _maxJumpsPerTick = value;
        }
    }

    public BehaviourResult Tick(float deltaTime)
    {
        if (float.IsNaN(deltaTime) || float.IsInfinity(deltaTime) || deltaTime < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(deltaTime), "Delta time must be finite and non-negative.");
        }

        if (_cancelled)
        {
            return new BehaviourResult(BehaviourStatus.Cancelled);
        }

        DeltaTime = deltaTime;
        var outcome = ExecuteFrame(_mainFrame, _tree.Root);
        Status = outcome.Status;
        return new BehaviourResult(outcome.Status, outcome.Output);
    }

    /// <summary>Stops execution, runs active <c>execAfter</c> hooks with <see cref="BehaviourContext.Interrupted"/>, and clears progress.</summary>
    public void Cancel()
    {
        if (_cancelled)
        {
            return;
        }

        DeactivateAll(_mainFrame);
        _mainFrame.Clear();
        foreach (var frame in _frames.Values)
        {
            DeactivateAll(frame);
            frame.Clear();
        }

        _frames.Clear();
        _cancelled = true;
        Status = BehaviourStatus.Cancelled;
    }

    /// <summary>Clears execution progress so the next tick starts at the root. State is kept unless requested.</summary>
    public void Reset(bool resetState = false)
    {
        DeactivateAll(_mainFrame);
        _mainFrame.Clear();
        foreach (var frame in _frames.Values)
        {
            DeactivateAll(frame);
            frame.Clear();
        }

        _frames.Clear();
        _cancelled = false;
        Status = BehaviourStatus.Running;
        DeltaTime = 0;

        if (!resetState)
        {
            return;
        }

        _state.Clear();
        foreach (var pair in _tree.InitialState)
        {
            _state[pair.Key] = BehaviourTree.Clone(pair.Value);
        }
    }

    private sealed class Frame
    {
        internal readonly Dictionary<BehaviourNode, NodeState> States = new();

        internal NodeState State(BehaviourNode node)
        {
            if (!States.TryGetValue(node, out var state))
            {
                state = new NodeState();
                States[node] = state;
            }

            return state;
        }

        internal void Clear() => States.Clear();
    }

    private sealed class NodeState
    {
        internal bool Active;
        internal int Cursor;
        internal int Selected = -1;
        internal Dictionary<string, object?>? Args;
    }

    private readonly struct Outcome
    {
        internal Outcome(BehaviourStatus status, object? output, BehaviourNode? jump)
        {
            Status = status;
            Output = output;
            Jump = jump;
        }

        internal BehaviourStatus Status { get; }

        internal object? Output { get; }

        internal BehaviourNode? Jump { get; }

        internal bool IsJump => Jump != null;

        internal static Outcome Success(object? output) => new(BehaviourStatus.Success, output, null);

        internal static Outcome Failure(object? output) => new(BehaviourStatus.Failure, output, null);

        internal static Outcome Running => new(BehaviourStatus.Running, null, null);

        internal static Outcome JumpTo(BehaviourNode target) => new(BehaviourStatus.Running, null, target);
    }

    private Outcome ExecuteFrame(Frame frame, BehaviourNode root)
    {
        var outcome = Step(frame, root);
        var jumps = 0;
        while (outcome.IsJump)
        {
            if (++jumps > _maxJumpsPerTick)
            {
                throw new BehaviourException(
                    $"Jump budget exceeded ({_maxJumpsPerTick} per tick); the behaviour is looping in '{_tree.SourcePath}'.");
            }

            outcome = Resume(frame, outcome.Jump!);
        }

        return outcome;
    }

    private Outcome Step(Frame frame, BehaviourNode node)
    {
        var state = frame.State(node);
        if (!state.Active)
        {
            state.Active = true;
            state.Cursor = 0;
            state.Selected = -1;
            state.Args = ResolveArguments(node);
            if (node.ExecBefore != null)
            {
                RunScript(node.ExecBefore, node, state, interrupted: false);
            }
        }

        if (node.Exec != null)
        {
            RunScript(node.Exec, node, state, interrupted: false);
        }

        Outcome outcome;
        switch (node.Kind)
        {
            case BehaviourNodeKind.Sequence:
                outcome = RunSequence(frame, node, state, state.Cursor, null);
                break;
            case BehaviourNodeKind.Selector:
                outcome = RunSelector(frame, node, state, state.Cursor, null);
                break;
            case BehaviourNodeKind.Random:
                outcome = RunRandom(frame, node, state, null);
                break;
            case BehaviourNodeKind.Action:
                outcome = RunAction(node, Context(node, state, interrupted: false));
                break;
            case BehaviourNodeKind.Condition:
                outcome = RunCondition(frame, node, state, -1, null);
                break;
            case BehaviourNodeKind.Goto:
                outcome = Outcome.JumpTo(node.JumpTarget!);
                break;
            case BehaviourNodeKind.Use:
                outcome = RunUse(node);
                break;
            default:
                throw new BehaviourException($"Unsupported node kind '{node.Kind}'.");
        }

        if (outcome.IsJump)
        {
            if (node.Kind == BehaviourNodeKind.Goto)
            {
                Finish(node, state, BehaviourStatus.Running, null, interrupted: false);
            }

            return outcome;
        }

        if (outcome.Status == BehaviourStatus.Running)
        {
            return outcome;
        }

        Finish(node, state, outcome.Status, outcome.Output, interrupted: false);
        return outcome;
    }

    private Outcome Resume(Frame frame, BehaviourNode target)
    {
        var path = ComputePath(target);
        var keep = new HashSet<BehaviourNode>();
        for (var i = 0; i < path.Count - 1; i++)
        {
            keep.Add(path[i]);
        }

        DeactivateStrays(frame, keep);

        for (var i = 0; i < path.Count - 1; i++)
        {
            var ancestor = path[i];
            var child = path[i + 1];
            var state = frame.State(ancestor);
            if (!state.Active)
            {
                state.Active = true;
                state.Cursor = 0;
                state.Selected = -1;
                state.Args = ResolveArguments(ancestor);
                if (ancestor.ExecBefore != null)
                {
                    RunScript(ancestor.ExecBefore, ancestor, state, interrupted: false);
                }
            }

            state.Cursor = child.ParentIndex;
            state.Selected = child.ParentIndex;
        }

        var outcome = Step(frame, target);
        if (outcome.IsJump)
        {
            return outcome;
        }

        for (var i = path.Count - 2; i >= 0; i--)
        {
            var parent = path[i];
            var child = path[i + 1];
            var state = frame.State(parent);
            outcome = ContinueAfter(frame, parent, state, child.ParentIndex, outcome);
            if (outcome.IsJump)
            {
                return outcome;
            }

            if (outcome.Status != BehaviourStatus.Running)
            {
                Finish(parent, state, outcome.Status, outcome.Output, interrupted: false);
            }
        }

        return outcome;
    }

    private Outcome ContinueAfter(Frame frame, BehaviourNode node, NodeState state, int index, Outcome child)
    {
        switch (node.Kind)
        {
            case BehaviourNodeKind.Sequence:
                return RunSequence(frame, node, state, index, child);
            case BehaviourNodeKind.Selector:
                return RunSelector(frame, node, state, index, child);
            case BehaviourNodeKind.Random:
                state.Selected = index;
                return RunRandom(frame, node, state, child);
            case BehaviourNodeKind.Condition:
                state.Selected = index;
                state.Cursor = index;
                return child;
            default:
                throw new BehaviourException($"Node '{node.Describe()}' cannot resume a child.");
        }
    }

    private Outcome RunSequence(Frame frame, BehaviourNode node, NodeState state, int startIndex, Outcome? seed)
    {
        var index = startIndex;
        var has = seed != null;
        var current = seed ?? default;

        while (true)
        {
            if (index >= node.Children.Count)
            {
                return Outcome.Success(null);
            }

            if (!has)
            {
                current = Step(frame, node.Children[index]);
            }

            has = false;
            state.Cursor = index;

            if (current.IsJump)
            {
                return current;
            }

            if (current.Status == BehaviourStatus.Running || current.Status == BehaviourStatus.Failure)
            {
                return current;
            }

            index++;
        }
    }

    private Outcome RunSelector(Frame frame, BehaviourNode node, NodeState state, int startIndex, Outcome? seed)
    {
        var index = startIndex;
        var has = seed != null;
        var current = seed ?? default;

        while (true)
        {
            if (index >= node.Children.Count)
            {
                return Outcome.Failure(null);
            }

            if (!has)
            {
                current = Step(frame, node.Children[index]);
            }

            has = false;
            state.Cursor = index;

            if (current.IsJump)
            {
                return current;
            }

            if (current.Status == BehaviourStatus.Running || current.Status == BehaviourStatus.Success)
            {
                return current;
            }

            index++;
        }
    }

    private Outcome RunRandom(Frame frame, BehaviourNode node, NodeState state, Outcome? seed)
    {
        var index = state.Selected >= 0 ? state.Selected : ChooseChild(node);
        state.Selected = index;
        state.Cursor = index;

        var outcome = seed ?? Step(frame, node.Children[index]);
        return outcome;
    }

    private Outcome RunCondition(Frame frame, BehaviourNode node, NodeState state, int selected, Outcome? seed)
    {
        var branchIndex = selected >= 0 ? selected : state.Selected;
        if (branchIndex < 0)
        {
            if (_condition == null)
            {
                throw new BehaviourException(
                    $"Node '{node.Describe()}' uses condition '{node.Condition}', but the instance has no condition evaluator.");
            }

            branchIndex = _condition(node.Condition!, Context(node, state, interrupted: false)) ? 0 : 1;
            state.Selected = branchIndex;
        }

        var branch = branchIndex == 0 ? node.WhenTrue : node.WhenFalse;
        if (branch == null)
        {
            return branchIndex == 0 ? Outcome.Success(null) : Outcome.Failure(null);
        }

        state.Cursor = branchIndex;
        return seed ?? Step(frame, branch);
    }

    private Outcome RunAction(BehaviourNode node, BehaviourContext context)
    {
        if (_action == null)
        {
            throw new BehaviourException(
                $"Node '{node.Describe()}' uses action '{node.Action}', but the instance has no action executor.");
        }

        var result = _action(node.Action!, context);
        if (result.Status == BehaviourStatus.Cancelled)
        {
            throw new BehaviourException(
                $"Action '{node.Action}' returned Cancelled; actions must return Success, Failure or Running.");
        }

        return new Outcome(result.Status, result.Output, null);
    }

    private Outcome RunUse(BehaviourNode node)
    {
        var imported = node.Use ??
                       throw new BehaviourException($"Node '{node.Describe()}' has no imported tree.");
        if (!_frames.TryGetValue(node, out var frame))
        {
            frame = new Frame();
            _frames.Add(node, frame);
        }

        var outcome = ExecuteFrame(frame, imported.Root);
        if (outcome.Status == BehaviourStatus.Success || outcome.Status == BehaviourStatus.Failure)
        {
            frame.Clear();
        }

        return outcome;
    }

    private int ChooseChild(BehaviourNode node)
    {
        var total = 0d;
        foreach (var child in node.Children)
        {
            if (child.Weight > 0)
            {
                total += child.Weight;
            }
        }

        var roll = NextRandom() * total;
        var cursor = 0d;
        var fallback = -1;
        for (var i = 0; i < node.Children.Count; i++)
        {
            var weight = node.Children[i].Weight;
            if (weight <= 0)
            {
                continue;
            }

            fallback = i;
            cursor += weight;
            if (roll < cursor)
            {
                return i;
            }
        }

        if (fallback < 0)
        {
            throw new BehaviourException($"Random node '{node.Describe()}' has no selectable child.");
        }

        return fallback;
    }

    private double NextRandom()
    {
        var value = _random();
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new BehaviourException("The random source returned a non-finite value.");
        }

        if (value < 0)
        {
            return 0;
        }

        return value >= 1 ? 0.9999999999999999 : value;
    }

    private static double NextSharedRandom()
    {
        lock (SharedRandom)
        {
            return SharedRandom.NextDouble();
        }
    }

    private void DeactivateStrays(Frame frame, HashSet<BehaviourNode> keep)
    {
        List<(BehaviourNode Node, NodeState State, int Depth)>? strays = null;
        foreach (var pair in frame.States)
        {
            if (!pair.Value.Active || keep.Contains(pair.Key))
            {
                continue;
            }

            (strays ??= new List<(BehaviourNode, NodeState, int)>()).Add((pair.Key, pair.Value, Depth(pair.Key)));
        }

        if (strays == null)
        {
            return;
        }

        strays.Sort((left, right) => right.Depth.CompareTo(left.Depth));
        foreach (var stray in strays)
        {
            Finish(stray.Node, stray.State, BehaviourStatus.Running, null, interrupted: true);
        }
    }

    private void DeactivateAll(Frame frame)
    {
        List<(BehaviourNode Node, NodeState State, int Depth)> active = new();
        foreach (var pair in frame.States)
        {
            if (pair.Value.Active)
            {
                active.Add((pair.Key, pair.Value, Depth(pair.Key)));
            }
        }

        active.Sort((left, right) => right.Depth.CompareTo(left.Depth));
        foreach (var item in active)
        {
            Finish(item.Node, item.State, BehaviourStatus.Running, null, interrupted: true);
        }
    }

    private static int Depth(BehaviourNode node)
    {
        var depth = 0;
        for (var current = node.Parent; current != null; current = current.Parent)
        {
            depth++;
        }

        return depth;
    }

    private static List<BehaviourNode> ComputePath(BehaviourNode target)
    {
        var path = new List<BehaviourNode>();
        for (var node = target; node != null; node = node.Parent)
        {
            path.Add(node);
        }

        path.Reverse();
        return path;
    }

    private void Finish(BehaviourNode node, NodeState state, BehaviourStatus status, object? output, bool interrupted)
    {
        if (!interrupted && status == BehaviourStatus.Success && node.Output != null)
        {
            AssignOutput(node, output);
        }

        if (node.Kind == BehaviourNodeKind.Use && _frames.TryGetValue(node, out var subFrame))
        {
            DeactivateAll(subFrame);
            subFrame.Clear();
        }

        if (node.ExecAfter != null)
        {
            RunScript(node.ExecAfter, node, state, interrupted);
        }

        state.Active = false;
        state.Cursor = 0;
        state.Selected = -1;
        state.Args = null;
    }

    private void RunScript(string code, BehaviourNode node, NodeState state, bool interrupted)
    {
        if (_execute == null)
        {
            throw new BehaviourException(
                $"Node '{node.Describe()}' declares an inline script, but the instance has no script executor.");
        }

        _execute(code, Context(node, state, interrupted));
    }

    private BehaviourContext Context(BehaviourNode node, NodeState state, bool interrupted) =>
        new(state.Args ?? NoArguments, _state, _host, DeltaTime, interrupted);

    private Dictionary<string, object?> ResolveArguments(BehaviourNode node)
    {
        if (node.Arguments.Count == 0)
        {
            return NoArguments;
        }

        var resolved = new Dictionary<string, object?>(node.Arguments.Count, StringComparer.Ordinal);
        foreach (var pair in node.Arguments)
        {
            resolved[pair.Key] = ResolveValue(pair.Value, node, pair.Key);
        }

        return resolved;
    }

    private object? ResolveValue(object? value, BehaviourNode node, string path)
    {
        if (value is string text && BehaviourTree.TryParseReference(text, out var segments))
        {
            return ReadState(segments, node, path);
        }

        if (value is Dictionary<string, object?> map)
        {
            var resolved = new Dictionary<string, object?>(map.Count, StringComparer.Ordinal);
            foreach (var pair in map)
            {
                resolved[pair.Key] = ResolveValue(pair.Value, node, path + "." + pair.Key);
            }

            return resolved;
        }

        if (value is List<object?> list)
        {
            var resolved = new List<object?>(list.Count);
            for (var i = 0; i < list.Count; i++)
            {
                resolved.Add(ResolveValue(list[i], node, path + "[" + i + "]"));
            }

            return resolved;
        }

        return value;
    }

    private object? ReadState(List<string> segments, BehaviourNode node, string path)
    {
        object? current = _state;
        foreach (var segment in segments)
        {
            if (current is Dictionary<string, object?> map)
            {
                if (!map.TryGetValue(segment, out current))
                {
                    throw new BehaviourException(
                        $"Node '{node.Describe()}' argument '{path}' references missing state '{Join(segments)}'.");
                }
            }
            else if (current is List<object?> list)
            {
                if (!int.TryParse(segment, out var index) || index < 0 || index >= list.Count)
                {
                    throw new BehaviourException(
                        $"Node '{node.Describe()}' argument '{path}' references invalid list index in '{Join(segments)}'.");
                }

                current = list[index];
            }
            else
            {
                throw new BehaviourException(
                    $"Node '{node.Describe()}' argument '{path}' cannot descend into state '{Join(segments)}'.");
            }
        }

        return current;
    }

    private void AssignOutput(BehaviourNode node, object? value)
    {
        var path = node.Output!;
        object container = _state;
        for (var i = 0; i < path.Count - 1; i++)
        {
            var segment = path[i];
            if (container is Dictionary<string, object?> map)
            {
                if (!map.TryGetValue(segment, out var next) || next == null)
                {
                    throw new BehaviourException(
                        $"Node '{node.Describe()}' out reference '{Join(path)}' has no state container at '{segment}'.");
                }

                container = next;
            }
            else if (container is List<object?> list)
            {
                if (!int.TryParse(segment, out var index) || index < 0 || index >= list.Count)
                {
                    throw new BehaviourException(
                        $"Node '{node.Describe()}' out reference '{Join(path)}' has an invalid list index '{segment}'.");
                }

                var next = list[index];
                if (next == null)
                {
                    throw new BehaviourException(
                        $"Node '{node.Describe()}' out reference '{Join(path)}' has no state container at '{segment}'.");
                }

                container = next;
            }
            else
            {
                throw new BehaviourException(
                    $"Node '{node.Describe()}' out reference '{Join(path)}' cannot descend at '{segment}'.");
            }
        }

        var last = path[path.Count - 1];
        if (container is Dictionary<string, object?> targetMap)
        {
            if (!targetMap.ContainsKey(last))
            {
                throw new BehaviourException(
                    $"Node '{node.Describe()}' out reference '{Join(path)}' is not declared in state.");
            }

            targetMap[last] = value;
            return;
        }

        if (container is List<object?> targetList)
        {
            if (!int.TryParse(last, out var index) || index < 0 || index >= targetList.Count)
            {
                throw new BehaviourException(
                    $"Node '{node.Describe()}' out reference '{Join(path)}' has an invalid list index '{last}'.");
            }

            targetList[index] = value;
            return;
        }

        throw new BehaviourException(
            $"Node '{node.Describe()}' out reference '{Join(path)}' cannot assign at '{last}'.");
    }

    private static string Join(IReadOnlyList<string> segments) => string.Join(".", segments);
}
