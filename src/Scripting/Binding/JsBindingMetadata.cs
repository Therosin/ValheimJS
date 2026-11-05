using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace ValheimJS.Scripting.Binding;

/// <summary>JavaScript type a C# parameter or return value maps to.</summary>
public enum JsTypeKind
{
    Void,
    Unknown,
    String,
    Number,
    Boolean,
    Array,
    Callback
}

/// <summary>Small type algebra used by both the bridge and declaration generation.</summary>
public sealed class JsTypeRef
{
    private JsTypeRef(JsTypeKind kind, JsTypeRef? element = null, bool nullable = false, string? declaredText = null)
    {
        Kind = kind;
        Element = element;
        Nullable = nullable;
        DeclaredText = declaredText;
    }

    public JsTypeKind Kind { get; }

    public JsTypeRef? Element { get; }

    public bool Nullable { get; }

    /// <summary>Author-supplied TypeScript spelling; wins over the inferred text.</summary>
    public string? DeclaredText { get; }

    public static JsTypeRef Void { get; } = new(JsTypeKind.Void);

    public static JsTypeRef Unknown { get; } = new(JsTypeKind.Unknown);

    public static JsTypeRef String { get; } = new(JsTypeKind.String);

    public static JsTypeRef Number { get; } = new(JsTypeKind.Number);

    public static JsTypeRef Boolean { get; } = new(JsTypeKind.Boolean);

    public static JsTypeRef Callback { get; } = new(JsTypeKind.Callback);

    public static JsTypeRef ArrayOf(JsTypeRef element) => new(JsTypeKind.Array, element);

    public static JsTypeRef Declared(string text) => new(JsTypeKind.Unknown, declaredText: text);

    public JsTypeRef AsNullable() => Nullable ? this : new JsTypeRef(Kind, Element, nullable: true, DeclaredText);

    public string ToTypeScript()
    {
        if (!string.IsNullOrEmpty(DeclaredText))
        {
            return DeclaredText!;
        }

        var text = Kind switch
        {
            JsTypeKind.Void => "void",
            JsTypeKind.String => "string",
            JsTypeKind.Number => "number",
            JsTypeKind.Boolean => "boolean",
            JsTypeKind.Array => Element!.ToTypeScript() + "[]",
            JsTypeKind.Callback => "(...args: unknown[]) => unknown",
            _ => "unknown"
        };

        return Nullable ? text + " | null" : text;
    }

    public override string ToString() => ToTypeScript();
}

/// <summary>One exposed method: exported name plus everything declarations need.</summary>
public sealed class JsMethodMetadata
{
    public JsMethodMetadata(
        string name,
        string sourceName,
        string? description,
        JsAvailability availability,
        JsReturnKind returnKind,
        bool scoped,
        IReadOnlyList<JsParameterMetadata> parameters,
        JsTypeRef returnType,
        MethodInfo method)
    {
        Name = name;
        SourceName = sourceName;
        Description = description;
        Availability = availability;
        ReturnKind = returnKind;
        Scoped = scoped;
        Parameters = parameters;
        ReturnType = returnType;
        Method = method;
    }

    public string Name { get; }

    public string SourceName { get; }

    public string? Description { get; }

    public JsAvailability Availability { get; }

    public JsReturnKind ReturnKind { get; }

    public bool Scoped { get; }

    /// <summary>Parameters visible to scripts; the injected plugin name is not included.</summary>
    public IReadOnlyList<JsParameterMetadata> Parameters { get; }

    public JsTypeRef ReturnType { get; }

    public MethodInfo Method { get; }
}

public sealed class JsParameterMetadata
{
    public JsParameterMetadata(string name, JsTypeRef type, Type clrType)
    {
        Name = name;
        Type = type;
        ClrType = clrType;
    }

    public string Name { get; }

    public JsTypeRef Type { get; }

    /// <summary>The CLR parameter type; used by the bridge to coerce numbers.</summary>
    public Type ClrType { get; }
}

/// <summary>One attributed binding class: its namespace and visible methods.</summary>
public sealed class JsBinderMetadata
{
    public JsBinderMetadata(
        string nameSpace,
        JsBindPhase phase,
        int order,
        JsAvailability availability,
        string? description,
        bool scoped,
        Type type,
        IReadOnlyList<JsMethodMetadata> methods)
    {
        Namespace = nameSpace;
        Phase = phase;
        Order = order;
        Availability = availability;
        Description = description;
        Scoped = scoped;
        Type = type;
        Methods = methods;
    }

    public string Namespace { get; }

    public JsBindPhase Phase { get; }

    public int Order { get; }

    public JsAvailability Availability { get; }

    public string? Description { get; }

    public bool Scoped { get; }

    public Type Type { get; }

    public IReadOnlyList<JsMethodMetadata> Methods { get; }
}

/// <summary>Result of discovery: what may be installed, and every reason it may not.</summary>
public sealed class JsBindingCatalog
{
    public JsBindingCatalog(IReadOnlyList<JsBinderMetadata> binders, IReadOnlyList<string> errors)
    {
        Binders = binders;
        Errors = errors;
    }

    public IReadOnlyList<JsBinderMetadata> Binders { get; }

    public IReadOnlyList<string> Errors { get; }

    public bool IsValid => Errors.Count == 0;

    public string DescribeErrors() => Errors.Count == 0
        ? string.Empty
        : string.Join(Environment.NewLine, Errors.Select(error => "  - " + error));
}

/// <summary>
/// Reflects over a plugin assembly and turns <see cref="JsBinderAttribute"/> /
/// <see cref="JsBindingAttribute"/> into a validated catalog. Only reflection primitives that
/// both the runtime and a metadata-only load context understand are used, so the same code
/// generates the checked-in declarations.
/// </summary>
public static class JsBindingDiscovery
{
    private static readonly Regex IdentifierPattern =
        new(@"^[A-Za-z_$][A-Za-z0-9_$]*(\.[A-Za-z_$][A-Za-z0-9_$]*)*$", RegexOptions.Compiled);

    private static readonly HashSet<string> NumberTypes = new(StringComparer.Ordinal)
    {
        "System.Byte", "System.SByte", "System.Int16", "System.UInt16", "System.Int32",
        "System.UInt32", "System.Int64", "System.UInt64", "System.Single", "System.Double",
        "System.Decimal"
    };

    /// <summary>
    /// Discovers every attributed binding class. <paramref name="include"/> lets tests keep
    /// deliberately invalid fixtures in the same assembly without failing the real catalog.
    /// </summary>
    public static JsBindingCatalog Discover(Assembly assembly, Func<Type, bool>? include = null)
    {
        if (assembly == null)
        {
            throw new ArgumentNullException(nameof(assembly));
        }

        var errors = new List<string>();
        var binders = new List<JsBinderMetadata>();
        var exported = new Dictionary<string, string>(StringComparer.Ordinal);
        var namespaces = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var type in SafeTypes(assembly))
        {
            if (include != null && !include(type))
            {
                continue;
            }

            var binder = Analyze(type, exported, namespaces, errors);
            if (binder != null)
            {
                binders.Add(binder);
            }
        }

        return new JsBindingCatalog(
            binders
                .OrderBy(item => item.Phase)
                .ThenBy(item => item.Order)
                .ThenBy(item => item.Namespace, StringComparer.Ordinal)
                .ToList(),
            errors);
    }

    /// <summary>Validates one type in isolation; used by discovery tests.</summary>
    public static JsBinderMetadata? DiscoverBinder(Type type, out IReadOnlyList<string> errors)
    {
        var collected = new List<string>();
        var binder = Analyze(
            type,
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            collected);
        errors = collected;
        return binder;
    }

    private static JsBinderMetadata? Analyze(
        Type type,
        Dictionary<string, string> exported,
        Dictionary<string, string> namespaces,
        List<string> errors)
    {
        {
            var binder = FindAttribute(type.GetCustomAttributesData(), "JsBinderAttribute");
            if (binder == null)
            {
                return null;
            }

            var typeName = type.FullName ?? type.Name;
            var nameSpace = binder.Value.String(0) ?? string.Empty;
            var where = $"[JsBinder:{nameSpace}] {typeName}";

            if (!IdentifierPattern.IsMatch(nameSpace))
            {
                errors.Add($"{where}: namespace must be a JavaScript identifier or dotted path.");
                return null;
            }

            if (namespaces.TryGetValue(nameSpace, out var previous))
            {
                errors.Add($"{where}: namespace '{nameSpace}' is already claimed by {previous}.");
                return null;
            }

            if (!type.IsClass || type.IsAbstract)
            {
                errors.Add($"{where}: must be a concrete class, not an abstract class or interface.");
                return null;
            }

            if (!DerivesFrom(type, "ValheimJS.Scripting.Binding.JsBindings"))
            {
                errors.Add($"{where}: must derive from JsBindings.");
                return null;
            }

            if (!HasContextConstructor(type))
            {
                errors.Add($"{where}: needs a constructor accepting JsBindingContext.");
                return null;
            }

            var binderScoped = binder.Value.Bool("Scoped");
            var binderAvailability = binder.Value.Availability("Availability");
            var methods = new List<JsMethodMetadata>();

            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var binding = FindAttribute(method.GetCustomAttributesData(), "JsBindingAttribute");
                if (binding == null)
                {
                    continue;
                }

                var methodWhere = $"{where}.{method.Name}";
                var methodScoped = binding.Value.Bool("Scoped");
                var parameters = method.GetParameters();

                if (method.IsStatic)
                {
                    errors.Add($"{methodWhere}: binding methods must be instance methods so they can use binder state.");
                    continue;
                }

                if (method.IsGenericMethodDefinition || method.ContainsGenericParameters)
                {
                    errors.Add($"{methodWhere}: generic methods cannot be bound; expose a concrete signature.");
                    continue;
                }

                if (parameters.Length > 12)
                {
                    errors.Add($"{methodWhere}: has {parameters.Length} parameters; the bridge supports at most 12.");
                    continue;
                }

                var exportedName = binding.Value.String(0);
                if (string.IsNullOrEmpty(exportedName))
                {
                    exportedName = CamelCase(method.Name);
                }

                if (!IdentifierPattern.IsMatch(exportedName!))
                {
                    errors.Add($"{methodWhere}: exported name '{exportedName}' must be a JavaScript identifier.");
                    continue;
                }

                var scoped = binderScoped || methodScoped;
                var visible = parameters.AsEnumerable();

                if (scoped)
                {
                    if (parameters.Length == 0 || FullName(parameters[0].ParameterType) != "System.String")
                    {
                        errors.Add($"{methodWhere}: scoped bindings need a first 'string plugin' parameter.");
                        continue;
                    }

                    visible = parameters.Skip(1);
                }

                var returnKind = binding.Value.ReturnKind("ReturnKind");
                var returnOverride = binding.Value.String("Returns");
                var parameterMetadata = new List<JsParameterMetadata>();
                var signatureValid = true;

                foreach (var parameter in visible)
                {
                    if (!TryMap(parameter.ParameterType, out var parameterType, out var reason))
                    {
                        errors.Add($"{methodWhere}: parameter '{parameter.Name}' {reason}");
                        signatureValid = false;
                        continue;
                    }

                    if (parameterType.Kind == JsTypeKind.Array && parameterType.Element!.Kind != JsTypeKind.String)
                    {
                        errors.Add($"{methodWhere}: parameter '{parameter.Name}' must be a string[]; other array element types have no bridge conversion.");
                        signatureValid = false;
                        continue;
                    }

                    parameterMetadata.Add(new JsParameterMetadata(parameter.Name ?? "arg", parameterType, parameter.ParameterType));
                }

                if (!signatureValid)
                {
                    continue;
                }

                JsTypeRef returnType;
                if (!string.IsNullOrEmpty(returnOverride))
                {
                    // Author-supplied spelling wins; it is documentation metadata only.
                    returnType = JsTypeRef.Declared(returnOverride!);
                }
                else if (!TryMap(method.ReturnType, out returnType, out var returnReason))
                {
                    errors.Add($"{methodWhere}: return value {returnReason}");
                    continue;
                }

                if (returnKind == JsReturnKind.Json && FullName(method.ReturnType) != "System.String")
                {
                    errors.Add($"{methodWhere}: ReturnKind.Json requires a string (JSON text) return type.");
                    continue;
                }

                if (returnKind == JsReturnKind.Native && string.IsNullOrEmpty(returnOverride))
                {
                    errors.Add($"{methodWhere}: ReturnKind.Native requires Returns = \"<TypeScript type>\" for declarations.");
                    continue;
                }

                var qualified = nameSpace + "." + exportedName;
                if (exported.TryGetValue(qualified, out var owner))
                {
                    errors.Add($"{methodWhere}: exported name '{qualified}' is already declared by {owner}.");
                    continue;
                }

                exported[qualified] = methodWhere;
                methods.Add(new JsMethodMetadata(
                    exportedName!,
                    method.Name,
                    binding.Value.String("Description"),
                    binding.Value.Availability("Availability"),
                    returnKind,
                    scoped,
                    parameterMetadata,
                    returnType,
                    method));
            }

            if (methods.Count == 0)
            {
                errors.Add($"{where}: declares no [JsBinding] methods; remove the attribute or expose the API.");
                return null;
            }

            namespaces[nameSpace] = typeName;
            return new JsBinderMetadata(
                nameSpace,
                binder.Value.Phase("Phase"),
                binder.Value.Int("Order"),
                binderAvailability,
                binder.Value.String("Description"),
                binderScoped,
                type,
                methods.OrderBy(method => method.Name, StringComparer.Ordinal).ToList());
        }
    }

    /// <summary>Maps a CLR type to the JavaScript type it crosses as.</summary>
    public static bool TryMap(Type type, out JsTypeRef mapped, out string reason)
    {
        mapped = JsTypeRef.Unknown;
        reason = string.Empty;

        var nullable = false;
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying != null)
        {
            nullable = true;
            type = underlying;
        }

        var name = FullName(type);

        switch (name)
        {
            case "System.Void":
                mapped = JsTypeRef.Void;
                return true;
            case "System.String":
                mapped = nullable ? JsTypeRef.String.AsNullable() : JsTypeRef.String;
                return true;
            case "System.Boolean":
                mapped = nullable ? JsTypeRef.Boolean.AsNullable() : JsTypeRef.Boolean;
                return true;
            case "ValheimJS.Scripting.Binding.JsCallback":
                mapped = JsTypeRef.Callback;
                return true;
            case "Jint.Native.JsValue":
                mapped = JsTypeRef.Unknown;
                return true;
        }

        if (NumberTypes.Contains(name))
        {
            mapped = nullable ? JsTypeRef.Number.AsNullable() : JsTypeRef.Number;
            return true;
        }

        if (type.IsArray && type.GetArrayRank() == 1)
        {
            if (!TryMap(type.GetElementType()!, out var element, out var elementReason))
            {
                reason = "array element " + elementReason;
                return false;
            }

            mapped = JsTypeRef.ArrayOf(element);
            return true;
        }

        reason = name.StartsWith("System.Threading.Tasks.", StringComparison.Ordinal)
            ? "is asynchronous; bindings run on the game thread and must return their value directly."
            : $"type '{name}' has no JavaScript mapping; use string/number/boolean/array/JsCallback or a JSON string.";
        return false;
    }

    public static string CamelCase(string value) =>
        string.IsNullOrEmpty(value) ? value : char.ToLowerInvariant(value[0]) + value.Substring(1);

    private static string FullName(Type type) => type.FullName ?? type.Name;

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes().Where(type => type != null)!;
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(type => type != null)!;
        }
    }

    private static bool DerivesFrom(Type type, string baseName)
    {
        for (var current = type.BaseType; current != null; current = current.BaseType)
        {
            if (FullName(current) == baseName)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasContextConstructor(Type type)
    {
        foreach (var constructor in type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            var parameters = constructor.GetParameters();
            if (parameters.Length == 1 &&
                FullName(parameters[0].ParameterType) == "ValheimJS.Scripting.Binding.JsBindingContext")
            {
                return true;
            }
        }

        return false;
    }

    private static AttributeView? FindAttribute(IList<CustomAttributeData> attributes, string simpleName)
    {
        foreach (var attribute in attributes)
        {
            if (attribute.AttributeType.Name == simpleName)
            {
                return new AttributeView(attribute);
            }
        }

        return null;
    }

    /// <summary>Attribute data in a form that works for runtime and metadata-only reflection.</summary>
    private readonly struct AttributeView
    {
        private readonly CustomAttributeData _data;

        public AttributeView(CustomAttributeData data) => _data = data;

        /// <summary>Positional constructor argument.</summary>
        public string? String(int position) =>
            _data.ConstructorArguments.Count > position
                ? _data.ConstructorArguments[position].Value as string
                : null;

        /// <summary>Named attribute property.</summary>
        public string? String(string named) => Named(named)?.Value as string;

        public bool Bool(string named) => Named(named)?.Value is bool value && value;

        public int Int(string named) => Named(named)?.Value is int value ? value : 0;

        public JsReturnKind ReturnKind(string named) =>
            Named(named)?.Value is int value ? (JsReturnKind)value : JsReturnKind.Raw;

        public JsBindPhase Phase(string named) =>
            Named(named)?.Value is int value ? (JsBindPhase)value : JsBindPhase.Gameplay;

        /// <summary>Flags/enum properties arrive as their underlying integer.</summary>
        public JsAvailability Availability(string named) =>
            Named(named)?.Value is int value ? (JsAvailability)value : JsAvailability.Always;

        private CustomAttributeTypedArgument? Named(string name)
        {
            foreach (var argument in _data.NamedArguments)
            {
                if (argument.MemberName == name)
                {
                    return argument.TypedValue;
                }
            }

            return null;
        }
    }
}
