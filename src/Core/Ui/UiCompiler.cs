using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ValheimJS.Utils;

namespace ValheimJS.Core;

/// <summary>Load/validation error for a UI document.</summary>
public sealed class UiException : Exception
{
    public UiException(string message)
        : base(message)
    {
    }

    public UiException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>Value carried by a panel property or component argument.</summary>
public enum UiValueKind
{
    Literal,
    Binding,
    Local,
    Param
}

/// <summary>
/// A property value that is either literal data or a reference resolved by the UI instance.
/// References survive component expansion; the compiler never evaluates a binding.
/// </summary>
public sealed class UiValue
{
    private UiValue(UiValueKind kind, object? literal, string? key)
    {
        Kind = kind;
        Literal = literal;
        Key = key;
    }

    public UiValueKind Kind { get; }

    public object? Literal { get; }

    public string? Key { get; }

    internal static UiValue LiteralValue(object? value) => new(UiValueKind.Literal, value, null);

    internal static UiValue Binding(string key) => new(UiValueKind.Binding, null, key);

    internal static UiValue Local(string key) => new(UiValueKind.Local, null, key);
}

/// <summary>Host-provided panel kinds. Plugins compose them; they cannot add new kinds.</summary>
public static class UiPanelTypes
{
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "window", "panel", "row", "column", "grid", "scroll",
        "spacer", "divider", "text", "image", "icon", "button",
        "input", "toggle", "slider", "select", "tabs", "tab",
        "section", "setting-row", "list-row", "footer"
    };

    public static bool IsKnown(string type) => Known.Contains(type);

    public static IReadOnlyCollection<string> All => Known;
}

/// <summary>One compiled panel node. Children and slots are already expanded.</summary>
public sealed class UiPanel
{
    internal UiPanel(
        string type,
        string? id,
        string path,
        string scope,
        IReadOnlyDictionary<string, object?> props,
        IReadOnlyList<UiPanel> children,
        IReadOnlyDictionary<string, object?> state)
    {
        Type = type;
        Id = id;
        Path = path;
        Scope = scope;
        Props = props;
        Children = children;
        State = state;
    }

    public string Type { get; }

    public string? Id { get; }

    /// <summary>Unique node path inside the open panel; used for events and widget lookup.</summary>
    public string Path { get; }

    /// <summary>Component-local state scope. Local references resolve here.</summary>
    public string Scope { get; }

    public IReadOnlyDictionary<string, object?> Props { get; }

    public IReadOnlyList<UiPanel> Children { get; }

    /// <summary>State defaults for this scope; empty when this node does not open a scope.</summary>
    public IReadOnlyDictionary<string, object?> State { get; }
}

/// <summary>A compiled YAML UI document: one root panel.</summary>
public sealed class UiDocument
{
    internal UiDocument(string source, UiPanel root)
    {
        Source = source;
        Root = root;
    }

    public string Source { get; }

    public UiPanel Root { get; }
}

/// <summary>
/// Compiles the panel YAML used by the <c>ui</c> binding. The compiler is Unity-free so the
/// harness can exercise expansion, imports, bindings, slots and limits without a game process.
/// </summary>
public static class UiCompiler
{
    private const int MaxNodes = 2048;
    private const int MaxDepth = 32;
    private const int MaxComponents = 128;
    private const int MaxYamlCharacters = 512 * 1024;
    private const int MaxKeyLength = 96;
    private const int MaxIdLength = 64;

    private static readonly Regex NamePattern = new("^[A-Za-z_][A-Za-z0-9_-]{0,63}$", RegexOptions.Compiled);

    private static readonly HashSet<string> TopLevelKeys = new(StringComparer.Ordinal)
    {
        "imports", "components", "panel"
    };

    private static readonly HashSet<string> PanelKeys = new(StringComparer.Ordinal)
    {
        "type", "use", "id", "props", "args", "children", "slots", "state"
    };

    /// <summary>
    /// Parses one panel document. <paramref name="importResolver"/> receives an import alias and
    /// returns YAML text; when imports are present and no resolver is supplied, loading fails.
    /// </summary>
    public static UiDocument Parse(
        string yaml,
        string? sourcePath = null,
        Func<string, string?>? importResolver = null)
    {
        if (yaml == null)
        {
            throw new ArgumentNullException(nameof(yaml));
        }

        if (yaml.Length > MaxYamlCharacters)
        {
            throw new UiException($"UI document exceeds {MaxYamlCharacters} characters.");
        }

        var source = string.IsNullOrWhiteSpace(sourcePath) ? "<ui>" : sourcePath;
        return new Loader(importResolver).CompileRoot(yaml, source);
    }

    private sealed class RawPanel
    {
        internal string? Type;
        internal string? Use;
        internal string? Id;
        internal string? Slot;
        internal string Location = string.Empty;
        internal Dictionary<string, object?> Props = new(StringComparer.Ordinal);
        internal Dictionary<string, object?> Args = new(StringComparer.Ordinal);
        internal List<RawPanel> Children = new();
        internal Dictionary<string, List<RawPanel>> Slots = new(StringComparer.Ordinal);
        internal Dictionary<string, object?> State = new(StringComparer.Ordinal);
    }

    private sealed class SlotSource
    {
        internal RawPanel Panel = null!;
        internal Dictionary<string, object?> Args = null!;
        internal string Scope = string.Empty;
        internal string Location = string.Empty;
    }

    private sealed class DocumentParts
    {
        internal Dictionary<string, RawPanel> Components = new(StringComparer.Ordinal);
        internal RawPanel? Panel;
        internal string Source = string.Empty;
    }

    private sealed class Loader
    {
        private readonly Func<string, string?>? _imports;
        private readonly Dictionary<string, DocumentParts> _cache = new(StringComparer.Ordinal);
        private readonly List<string> _stack = new();

        internal Loader(Func<string, string?>? imports) => _imports = imports;

        internal UiDocument CompileRoot(string yaml, string source)
        {
            var parts = Build(yaml, source, requirePanel: true);
            var context = new ExpandContext(parts.Components, source);
            var root = context.ExpandRoot(parts.Panel!);
            return new UiDocument(source, root);
        }

        private DocumentParts Build(string yaml, string source, bool requirePanel)
        {
            object? document;
            try
            {
                document = Yaml.Parse(yaml, source);
            }
            catch (YamlException exception)
            {
                throw new UiException(exception.Message, exception);
            }

            if (document is not YamlMap map)
            {
                throw new UiException($"{source}: the document root must be a mapping.");
            }

            foreach (var pair in map)
            {
                if (!TopLevelKeys.Contains(pair.Key))
                {
                    throw new UiException($"{source}: unknown key '{pair.Key}'.");
                }
            }

            var components = new Dictionary<string, RawPanel>(StringComparer.Ordinal);

            if (map.TryGetValue("imports", out var importsValue) && importsValue != null)
            {
                if (importsValue is not YamlMap imports)
                {
                    throw new UiException($"{source}: 'imports' must be a mapping of alias to path.");
                }

                foreach (var pair in imports)
                {
                    if (!NamePattern.IsMatch(pair.Key))
                    {
                        throw new UiException($"{source}: import alias '{pair.Key}' is not a valid name.");
                    }

                    if (pair.Value is not string path || path.Trim().Length == 0)
                    {
                        throw new UiException($"{source}: import '{pair.Key}' must be a file path.");
                    }

                    var imported = LoadAlias(pair.Key, path, source);
                    MergeComponents(components, imported.Components, source, pair.Key);
                    if (imported.Panel != null)
                    {
                        AddComponent(components, pair.Key, imported.Panel, source, "imported panel");
                    }
                }
            }

            if (map.TryGetValue("components", out var componentsValue) && componentsValue != null)
            {
                if (componentsValue is not YamlMap componentMap)
                {
                    throw new UiException($"{source}: 'components' must be a mapping.");
                }

                foreach (var pair in componentMap)
                {
                    if (!NamePattern.IsMatch(pair.Key))
                    {
                        throw new UiException($"{source}: component name '{pair.Key}' is not valid.");
                    }

                    var panel = ParsePanel(pair.Value, $"{source}/components/{pair.Key}", allowSlot: false);
                    AddComponent(components, pair.Key, panel, source, "component");
                }
            }

            RawPanel? root = null;
            if (map.TryGetValue("panel", out var rootValue) && rootValue != null)
            {
                root = ParsePanel(rootValue, $"{source}/panel", allowSlot: false);
            }
            else if (requirePanel)
            {
                throw new UiException($"{source}: missing 'panel'.");
            }

            return new DocumentParts { Components = components, Panel = root, Source = source };
        }

        private DocumentParts LoadAlias(string alias, string path, string importer)
        {
            if (_cache.TryGetValue(alias, out var cached))
            {
                return cached;
            }

            foreach (var active in _stack)
            {
                if (string.Equals(active, alias, StringComparison.Ordinal))
                {
                    throw new UiException(
                        $"Circular UI import: '{alias}' is already loading ({string.Join(" -> ", _stack)} -> {alias}).");
                }
            }

            if (_imports == null)
            {
                throw new UiException(
                    $"{importer}: import '{alias}' ({path}) needs an import resolver; none was supplied.");
            }

            var text = _imports(alias);
            if (text == null)
            {
                throw new UiException($"{importer}: import '{alias}' ({path}) was not supplied.");
            }

            _stack.Add(alias);
            try
            {
                var parts = Build(text, alias, requirePanel: false);
                _cache[alias] = parts;
                return parts;
            }
            finally
            {
                _stack.RemoveAt(_stack.Count - 1);
            }
        }

        private static void MergeComponents(
            Dictionary<string, RawPanel> target,
            IReadOnlyDictionary<string, RawPanel> source,
            string owner,
            string alias)
        {
            foreach (var pair in source)
            {
                AddComponent(target, pair.Key, pair.Value, owner, $"import '{alias}'");
            }
        }

        private static void AddComponent(
            Dictionary<string, RawPanel> target,
            string name,
            RawPanel panel,
            string owner,
            string kind)
        {
            if (target.ContainsKey(name))
            {
                throw new UiException($"{owner}: {kind} '{name}' collides with another component.");
            }

            if (target.Count >= MaxComponents)
            {
                throw new UiException($"{owner}: too many components (max {MaxComponents}).");
            }

            target[name] = panel;
        }

        private static string? OptionalString(YamlMap map, string key, string location)
        {
            if (!map.TryGetValue(key, out var value) || value == null)
            {
                return null;
            }

            if (value is not string text || text.Trim().Length == 0)
            {
                throw new UiException($"{location}: '{key}' must be a non-empty string.");
            }

            return text.Trim();
        }

        private static RawPanel ParsePanel(object? value, string location, bool allowSlot)
        {
            if (value is not YamlMap map)
            {
                throw new UiException($"{location}: a panel must be a mapping.");
            }

            if (map.ContainsKey("slot"))
            {
                if (!allowSlot)
                {
                    throw new UiException($"{location}: 'slot' is only valid inside a component definition.");
                }

                if (map.Count != 1 || map["slot"] is not string slot || !NamePattern.IsMatch(slot))
                {
                    throw new UiException($"{location}: 'slot' must be a single valid slot name.");
                }

                return new RawPanel { Slot = slot, Location = location };
            }

            foreach (var pair in map)
            {
                if (!PanelKeys.Contains(pair.Key))
                {
                    throw new UiException($"{location}: unknown panel key '{pair.Key}'.");
                }
            }

            var type = OptionalString(map, "type", location);
            var use = OptionalString(map, "use", location);
            if (type == null && use == null)
            {
                throw new UiException($"{location}: a panel needs 'type' or 'use'.");
            }

            if (type != null && use != null)
            {
                throw new UiException($"{location}: a panel cannot have both 'type' and 'use'.");
            }

            if (type != null && !UiPanelTypes.IsKnown(type))
            {
                throw new UiException($"{location}: unknown panel type '{type}'.");
            }

            if (use != null && !NamePattern.IsMatch(use))
            {
                throw new UiException($"{location}: component name '{use}' is not valid.");
            }

            var id = OptionalString(map, "id", location);
            if (id != null && id.Length > MaxIdLength)
            {
                throw new UiException($"{location}: id exceeds {MaxIdLength} characters.");
            }

            var panel = new RawPanel
            {
                Type = type,
                Use = use,
                Id = id,
                Location = location
            };

            if (map.TryGetValue("props", out var propsValue) && propsValue != null)
            {
                panel.Props = ParseMap(propsValue, $"{location}/props", allowReferences: true);
            }

            if (map.TryGetValue("args", out var argsValue) && argsValue != null)
            {
                if (use == null)
                {
                    throw new UiException($"{location}: 'args' is only valid with 'use'.");
                }

                panel.Args = ParseMap(argsValue, $"{location}/args", allowReferences: true);
            }

            if (map.TryGetValue("state", out var stateValue) && stateValue != null)
            {
                panel.State = ParseMap(stateValue, $"{location}/state", allowReferences: false);
            }

            if (map.TryGetValue("children", out var childrenValue) && childrenValue != null)
            {
                if (childrenValue is not List<object?> children)
                {
                    throw new UiException($"{location}: 'children' must be a sequence.");
                }

                for (var index = 0; index < children.Count; index++)
                {
                    panel.Children.Add(ParsePanel(children[index], $"{location}/children[{index}]", allowSlot: true));
                }
            }

            if (map.TryGetValue("slots", out var slotsValue) && slotsValue != null)
            {
                if (use == null)
                {
                    throw new UiException($"{location}: 'slots' is only valid with 'use'.");
                }

                if (slotsValue is not YamlMap slots)
                {
                    throw new UiException($"{location}: 'slots' must be a mapping.");
                }

                foreach (var pair in slots)
                {
                    if (!NamePattern.IsMatch(pair.Key))
                    {
                        throw new UiException($"{location}: slot name '{pair.Key}' is not valid.");
                    }

                    if (pair.Value is not List<object?> items)
                    {
                        throw new UiException($"{location}: slot '{pair.Key}' must be a sequence.");
                    }

                    var panels = new List<RawPanel>(items.Count);
                    for (var index = 0; index < items.Count; index++)
                    {
                        panels.Add(ParsePanel(items[index], $"{location}/slots/{pair.Key}[{index}]", allowSlot: false));
                    }

                    panel.Slots[pair.Key] = panels;
                }
            }

            return panel;
        }

        private static Dictionary<string, object?> ParseMap(object? value, string location, bool allowReferences)
        {
            if (value is not YamlMap map)
            {
                throw new UiException($"{location}: expected a mapping.");
            }

            var result = new Dictionary<string, object?>(map.Count, StringComparer.Ordinal);
            foreach (var pair in map)
            {
                if (pair.Key.Length > MaxKeyLength)
                {
                    throw new UiException($"{location}: key '{pair.Key}' is too long.");
                }

                result[pair.Key] = ParseValue(pair.Value, $"{location}/{pair.Key}", allowReferences);
            }

            return result;
        }

        private static object? ParseValue(object? value, string location, bool allowReferences)
        {
            if (value is YamlMap map)
            {
                if (map.Count == 1)
                {
                    foreach (var pair in map)
                    {
                        if (pair.Key is "param" or "bind" or "local")
                        {
                            if (!allowReferences)
                            {
                                throw new UiException($"{location}: references are not allowed here.");
                            }

                            if (pair.Value is not string key || key.Trim().Length == 0)
                            {
                                throw new UiException($"{location}: '{pair.Key}' needs a non-empty string key.");
                            }

                            key = key.Trim();
                            if (key.Length > MaxKeyLength)
                            {
                                throw new UiException($"{location}: reference key is too long.");
                            }

                            return pair.Key switch
                            {
                                "param" => new UiValueReference(UiValueKind.Param, key),
                                "bind" => UiValue.Binding(key),
                                _ => UiValue.Local(key)
                            };
                        }
                    }
                }

                var result = new Dictionary<string, object?>(map.Count, StringComparer.Ordinal);
                foreach (var pair in map)
                {
                    result[pair.Key] = ParseValue(pair.Value, $"{location}/{pair.Key}", allowReferences);
                }

                return result;
            }

            if (value is List<object?> list)
            {
                var result = new List<object?>(list.Count);
                for (var index = 0; index < list.Count; index++)
                {
                    result.Add(ParseValue(list[index], $"{location}[{index}]", allowReferences));
                }

                return result;
            }

            return value;
        }
    }

    /// <summary>Compiler-only marker for a component parameter reference.</summary>
    private sealed class UiValueReference
    {
        internal UiValueReference(UiValueKind kind, string key)
        {
            Kind = kind;
            Key = key;
        }

        internal UiValueKind Kind { get; }

        internal string Key { get; }
    }

    private sealed class ExpandContext
    {
        private readonly IReadOnlyDictionary<string, RawPanel> _components;
        private readonly string _source;
        private readonly List<string> _componentStack = new();
        private int _nodes;

        internal ExpandContext(IReadOnlyDictionary<string, RawPanel> components, string source)
        {
            _components = components;
            _source = source;
        }

        internal UiPanel ExpandRoot(RawPanel root)
        {
            var path = root.Id ?? "panel";
            return Expand(root, path, string.Empty, new Dictionary<string, object?>(StringComparer.Ordinal), null, 0, isComponentRoot: false);
        }

        private UiPanel Expand(
            RawPanel raw,
            string path,
            string scope,
            Dictionary<string, object?> args,
            Dictionary<string, List<SlotSource>>? slots,
            int depth,
            bool isComponentRoot)
        {
            if (++_nodes > MaxNodes)
            {
                throw new UiException($"{_source}: UI exceeds {MaxNodes} nodes.");
            }

            if (depth > MaxDepth)
            {
                throw new UiException($"{raw.Location}: UI nesting exceeds {MaxDepth} levels.");
            }

            if (raw.Slot != null)
            {
                throw new UiException($"{raw.Location}: a slot marker cannot be a root or direct value.");
            }

            if (raw.Use != null)
            {
                if (!_components.TryGetValue(raw.Use, out var component))
                {
                    throw new UiException($"{raw.Location}: unknown component '{raw.Use}'.");
                }

                if (_componentStack.Contains(raw.Use))
                {
                    throw new UiException(
                        $"{raw.Location}: component cycle: {string.Join(" -> ", _componentStack)} -> {raw.Use}.");
                }

                var componentArgs = ResolveMap(raw.Args, args, raw.Location);
                var slotSources = BuildSlots(raw, args, scope);
                _componentStack.Add(raw.Use);
                try
                {
                    return Expand(component, path, path, componentArgs, slotSources, depth + 1, isComponentRoot: true);
                }
                finally
                {
                    _componentStack.RemoveAt(_componentStack.Count - 1);
                }
            }

            var nodeScope = raw.State.Count > 0 || isComponentRoot ? path : scope;
            var resolvedProps = ResolveMap(raw.Props, args, raw.Location);
            var children = ExpandChildren(raw.Children, path, nodeScope, args, slots, depth);
            return new UiPanel(
                raw.Type!,
                raw.Id,
                path,
                nodeScope,
                resolvedProps,
                children,
                new Dictionary<string, object?>(raw.State, StringComparer.Ordinal));
        }

        private List<UiPanel> ExpandChildren(
            List<RawPanel> raws,
            string parentPath,
            string scope,
            Dictionary<string, object?> args,
            Dictionary<string, List<SlotSource>>? slots,
            int depth)
        {
            var result = new List<UiPanel>();
            for (var index = 0; index < raws.Count; index++)
            {
                var raw = raws[index];
                if (raw.Slot != null)
                {
                    if (slots == null || !slots.TryGetValue(raw.Slot, out var sources))
                    {
                        throw new UiException($"{raw.Location}: slot '{raw.Slot}' was not supplied.");
                    }

                    for (var slotIndex = 0; slotIndex < sources.Count; slotIndex++)
                    {
                        var source = sources[slotIndex];
                        var slotPath = $"{parentPath}/{raw.Slot}[{slotIndex}]";
                        result.Add(Expand(
                            source.Panel,
                            slotPath,
                            source.Scope,
                            source.Args,
                            null,
                            depth + 1,
                            isComponentRoot: false));
                    }

                    continue;
                }

                var segment = raw.Id ?? raw.Type ?? raw.Use ?? "panel";
                result.Add(Expand(
                    raw,
                    $"{parentPath}/{segment}[{index}]",
                    scope,
                    args,
                    slots,
                    depth + 1,
                    isComponentRoot: false));
            }

            return result;
        }

        private Dictionary<string, List<SlotSource>> BuildSlots(
            RawPanel use,
            Dictionary<string, object?> args,
            string scope)
        {
            var slots = new Dictionary<string, List<SlotSource>>(StringComparer.Ordinal);
            if (use.Children.Count > 0)
            {
                slots["children"] = use.Children
                    .Select(child => new SlotSource { Panel = child, Args = args, Scope = scope, Location = child.Location })
                    .ToList();
            }

            foreach (var pair in use.Slots)
            {
                slots[pair.Key] = pair.Value
                    .Select(child => new SlotSource { Panel = child, Args = args, Scope = scope, Location = child.Location })
                    .ToList();
            }

            return slots;
        }

        private Dictionary<string, object?> ResolveMap(
            Dictionary<string, object?> source,
            Dictionary<string, object?> args,
            string location)
        {
            var result = new Dictionary<string, object?>(source.Count, StringComparer.Ordinal);
            foreach (var pair in source)
            {
                result[pair.Key] = ResolveValue(pair.Value, args, $"{location}/{pair.Key}");
            }

            return result;
        }

        private object? ResolveValue(object? value, Dictionary<string, object?> args, string location)
        {
            if (value is UiValueReference reference)
            {
                if (!args.TryGetValue(reference.Key, out var argument))
                {
                    throw new UiException($"{location}: component argument '{reference.Key}' was not supplied.");
                }

                return argument;
            }

            if (value is Dictionary<string, object?> map)
            {
                var result = new Dictionary<string, object?>(map.Count, StringComparer.Ordinal);
                foreach (var pair in map)
                {
                    result[pair.Key] = ResolveValue(pair.Value, args, $"{location}/{pair.Key}");
                }

                return result;
            }

            if (value is List<object?> list)
            {
                var result = new List<object?>(list.Count);
                for (var index = 0; index < list.Count; index++)
                {
                    result.Add(ResolveValue(list[index], args, $"{location}[{index}]"));
                }

                return result;
            }

            return value;
        }
    }
}
