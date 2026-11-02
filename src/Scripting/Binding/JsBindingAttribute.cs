using System;

namespace ValheimJS.Scripting.Binding;

/// <summary>Install order. Lower phases run first, like a boot script with sections.</summary>
public enum JsBindPhase
{
    Core = 0,
    Engine = 100,
    Gameplay = 200,
    Mods = 1000
}

/// <summary>
/// Marks a class as a JS binding module. The class must expose
/// <c>static void Bind(JsBindingContext context)</c>; the loader discovers it by reflection,
/// so adding a game API to the script environment means adding one file under Bindings/.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class JsBindingAttribute : Attribute
{
    public JsBindingAttribute(string name, JsBindPhase phase = JsBindPhase.Gameplay, int order = 0)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("binding name cannot be empty", nameof(name));
        }

        Name = name;
        Phase = phase;
        Order = order;
    }

    public string Name { get; }

    public JsBindPhase Phase { get; }

    public int Order { get; }
}
