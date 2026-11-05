using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ValheimJS.Scripting.Binding;

namespace ValheimJS.Scripting.Types;

/// <summary>
/// Reflection-only view of <c>[JsType]</c> classes, used to generate TypeScript declarations from
/// the same attributes the registry consumes. No engine is required, so declarations cannot
/// drift from the registered surface.
/// </summary>
public static class JsTypeDiscovery
{
    public static IReadOnlyList<JsTypeMetadata> Discover(Assembly assembly, Func<Type, bool>? include = null)
    {
        var types = new List<JsTypeMetadata>();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var type in SafeTypes(assembly))
        {
            if (include != null && !include(type))
            {
                continue;
            }

            var attribute = Find(type.GetCustomAttributesData(), "JsTypeAttribute");
            if (attribute == null)
            {
                continue;
            }

            var name = CtorString(attribute, 0) ?? type.Name;
            if (names.TryGetValue(name, out var previous))
            {
                throw new InvalidOperationException($"[JsType] '{name}' is declared by both {previous} and {type.FullName}.");
            }

            names[name] = type.FullName ?? type.Name;
            types.Add(Analyze(type, name, attribute));
        }

        return types;
    }

    private static JsTypeMetadata Analyze(Type type, string name, CustomAttributeData attribute)
    {
        var members = new List<JsTypeMember>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (Find(property.GetCustomAttributesData(), "JsIgnoreAttribute") != null || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            var memberName = MemberName(property, property.Name);
            if (!seen.Add(memberName))
            {
                continue;
            }

            if (!JsClassBuilder.SupportsType(property.PropertyType))
            {
                continue;
            }

            JsBindingDiscovery.TryMap(property.PropertyType, out var mapped, out _);

            var propertyType = mapped.ToTypeScript();
            if (Find(property.GetCustomAttributesData(), "JsNullableAttribute") != null && !propertyType.EndsWith("| null", StringComparison.Ordinal))
            {
                propertyType += " | null";
            }

            members.Add(new JsTypeMember(memberName, propertyType, property.CanWrite, isMethod: false, parameters: Array.Empty<string>()));
        }

        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (Find(method.GetCustomAttributesData(), "JsIgnoreAttribute") != null || method.IsSpecialName ||
                method.DeclaringType == null || method.DeclaringType.FullName == "System.Object")
            {
                continue;
            }

            if (!JsClassBuilder.HasSupportedSignature(method, out _))
            {
                continue;
            }

            var memberName = MemberName(method, method.Name);
            if (!seen.Add(memberName))
            {
                continue;
            }

            var parameters = method.GetParameters().Select(parameter =>
            {
                JsBindingDiscovery.TryMap(parameter.ParameterType, out var mapped, out _);
                return (parameter.Name ?? "arg") + ": " + mapped.ToTypeScript();
            }).ToList();

            JsBindingDiscovery.TryMap(method.ReturnType, out var returnType, out _);
            var methodType = returnType.ToTypeScript();
            if (Find(method.GetCustomAttributesData(), "JsNullableAttribute") != null && !methodType.EndsWith("| null", StringComparison.Ordinal))
            {
                methodType += " | null";
            }

            members.Add(new JsTypeMember(memberName, methodType, writable: true, isMethod: true, parameters));
        }

        return new JsTypeMetadata(
            name,
            NamedString(attribute, "Description"),
            NamedAvailability(attribute),
            type.FullName ?? type.Name,
            members);
    }

    private static string MemberName(PropertyInfo property, string fallback) =>
        CtorString(Find(property.GetCustomAttributesData(), "JsNameAttribute"), 0) ?? CamelCase(fallback);

    private static string MemberName(MethodInfo method, string fallback) =>
        CtorString(Find(method.GetCustomAttributesData(), "JsNameAttribute"), 0) ?? CamelCase(fallback);

    private static CustomAttributeData? Find(IEnumerable<CustomAttributeData> attributes, string name) =>
        attributes.FirstOrDefault(attribute =>
            attribute.AttributeType.Name == name || attribute.AttributeType.FullName == name);

    private static string? CtorString(CustomAttributeData? attribute, int index) =>
        attribute != null && attribute.ConstructorArguments.Count > index && attribute.ConstructorArguments[index].Value is string text
            ? text
            : null;

    private static string? NamedString(CustomAttributeData? attribute, string name)
    {
        if (attribute == null)
        {
            return null;
        }

        foreach (var argument in attribute.NamedArguments)
        {
            if (argument.MemberName == name)
            {
                return argument.TypedValue.Value as string;
            }
        }

        return null;
    }

    private static JsAvailability NamedAvailability(CustomAttributeData attribute)
    {
        foreach (var argument in attribute.NamedArguments)
        {
            if (argument.MemberName != "Availability" || argument.TypedValue.Value == null)
            {
                continue;
            }

            var value = argument.TypedValue.Value;
            return value is JsAvailability availability
                ? availability
                : (JsAvailability)System.Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        return JsAvailability.Always;
    }

    private static string CamelCase(string name) =>
        name.Length > 0 && char.IsUpper(name[0]) ? char.ToLowerInvariant(name[0]) + name.Substring(1) : name;

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(type => type != null)!;
        }
    }
}

public sealed class JsTypeMetadata
{
    public JsTypeMetadata(string name, string? description, JsAvailability availability, string clrName, IReadOnlyList<JsTypeMember> members)
    {
        Name = name;
        Description = description;
        Availability = availability;
        ClrName = clrName;
        Members = members;
    }

    public string Name { get; }

    public string? Description { get; }

    public JsAvailability Availability { get; }

    public string ClrName { get; }

    public IReadOnlyList<JsTypeMember> Members { get; }
}

public sealed class JsTypeMember
{
    public JsTypeMember(string name, string type, bool writable, bool isMethod, IReadOnlyList<string> parameters)
    {
        Name = name;
        Type = type;
        Writable = writable;
        IsMethod = isMethod;
        Parameters = parameters;
    }

    public string Name { get; }

    public string Type { get; }

    public bool Writable { get; }

    public bool IsMethod { get; }

    public IReadOnlyList<string> Parameters { get; }
}
