using System;
using ValheimJS.Scripting.Binding;

namespace ValheimJS.Scripting.Types;

/// <summary>
/// Marks a native facade class as a JavaScript type. The name is what scripts see
/// (<c>ValheimBaseNPC</c>), and may differ from the CLR type name.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class JsTypeAttribute : Attribute
{
    public JsTypeAttribute(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A JavaScript type name is required.", nameof(name));
        }

        Name = name;
    }

    /// <summary>Name scripts see.</summary>
    public string Name { get; }

    /// <summary>One-paragraph description used in generated declarations.</summary>
    public string? Description { get; set; }

    /// <summary>Side availability used in generated declarations.</summary>
    public JsAvailability Availability { get; set; } = JsAvailability.Always;
}

/// <summary>Renames one property or method on a registered type.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Method, Inherited = false)]
public sealed class JsNameAttribute : Attribute
{
    public JsNameAttribute(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A JavaScript member name is required.", nameof(name));
        }

        Name = name;
    }

    public string Name { get; }
}

/// <summary>Marks a reference member as nullable in generated declarations.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Method | AttributeTargets.Parameter, Inherited = false)]
public sealed class JsNullableAttribute : Attribute
{
}

/// <summary>Keeps a public member out of the JavaScript surface.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Method | AttributeTargets.Field, Inherited = false)]
public sealed class JsIgnoreAttribute : Attribute
{
}

/// <summary>
/// Overrides the generated TypeScript return/member type for a <c>[JsType]</c> member that
/// returns a native <c>JsValue</c>. Runtime exposure is unchanged; declarations stay truthful.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Method, Inherited = false)]
public sealed class JsReturnsAttribute : Attribute
{
    public JsReturnsAttribute(string type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            throw new ArgumentException("A TypeScript type is required.", nameof(type));
        }

        Type = type;
    }

    public string Type { get; }
}
