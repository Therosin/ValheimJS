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

/// <summary>Which process sides a binder or method exists on.</summary>
[Flags]
public enum JsAvailability
{
    /// <summary>Every process: dedicated server, client, and single-player host.</summary>
    Always = 0,

    /// <summary>Dedicated-server processes. Use a live authority check for player hosts.</summary>
    Server = 1,

    /// <summary>Processes with a local player/UI (anything that is not a dedicated server).</summary>
    Client = 2,

    /// <summary>Headless dedicated servers only.</summary>
    Dedicated = 4
}

/// <summary>
/// Marks a class as a JavaScript binding module. The namespace is the property the binder
/// occupies under <c>host.api</c>; methods opt in with <see cref="JsBindingAttribute"/>.
/// <para>
/// The class must derive from <see cref="JsBindings"/> and take a
/// <see cref="JsBindingContext"/> through its constructor. Discovery rejects anything else
/// with an actionable diagnostic instead of guessing.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class JsBinderAttribute : Attribute
{
    public JsBinderAttribute(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A binder namespace is required.", nameof(name));
        }

        Name = name;
    }

    /// <summary>Property name under <c>host.api</c>, for example <c>world</c>.</summary>
    public string Name { get; }

    /// <summary>Install order; lower phases install first.</summary>
    public JsBindPhase Phase { get; set; } = JsBindPhase.Gameplay;

    /// <summary>Tie-breaker within a phase.</summary>
    public int Order { get; set; }

    /// <summary>Sides this whole binder is available on.</summary>
    public JsAvailability Availability { get; set; } = JsAvailability.Always;

    /// <summary>One-paragraph description used in generated declarations.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// When true, every method receives the calling plugin's name as its first argument and the
    /// SDK exposes the matching per-plugin view. Use for anything that must be sandboxed.
    /// </summary>
    public bool Scoped { get; set; }
}

/// <summary>
/// Exposes an ordinary C# method to JavaScript. The exported name defaults to the camel-case
/// method name; <see cref="Name"/> overrides it. Parameters and the return type are read from
/// the signature, so declarations cannot drift from the implementation.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class JsBindingAttribute : Attribute
{
    public JsBindingAttribute()
    {
    }

    public JsBindingAttribute(string name)
    {
        Name = name;
    }

    /// <summary>Exported name. Defaults to the camel-case method name.</summary>
    public string? Name { get; }

    /// <summary>Description shown in generated declarations and API docs.</summary>
    public string? Description { get; set; }

    /// <summary>How the return value crosses into JavaScript.</summary>
    public JsReturnKind ReturnKind { get; set; } = JsReturnKind.Raw;

    /// <summary>
    /// Injects the calling plugin's name as the first argument. The C# signature must start
    /// with a <c>string</c> parameter; it is never visible to scripts.
    /// </summary>
    public bool Scoped { get; set; }

    /// <summary>
    /// Sides this method exists on. Narrower than the binder's availability.
    /// </summary>
    public JsAvailability Availability { get; set; } = JsAvailability.Always;

    /// <summary>
    /// TypeScript spelling of a structured return value, for declarations only. Use with
    /// <see cref="JsReturnKind.Json"/>, for example <c>"WorldSnapshot"</c>.
    /// </summary>
    public string? Returns { get; set; }
}

/// <summary>How a binding's return value crosses into JS.</summary>
public enum JsReturnKind
{
    /// <summary>A primitive, array, or value already understood by the bridge.</summary>
    Raw,

    /// <summary>JSON text; the bridge parses it so scripts receive a real object.</summary>
    Json,

    /// <summary>
    /// A <c>JsValue</c>/<c>JsObject</c> built natively (see <c>JsPayload</c>); passed through
    /// untouched. Prefer this over <see cref="Json"/>: no string round trip, no duplicated field
    /// names. Declarations require <c>Returns</c>.
    /// </summary>
    Native
}

/// <summary>
/// Base class for binding modules. Constructed once per engine with the host context, it may
/// keep instance state in ordinary fields and release resources in <see cref="Dispose"/>.
/// </summary>
public abstract class JsBindings : IDisposable
{
    protected JsBindings(JsBindingContext context)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
    }

    protected JsBindingContext Context { get; }

    /// <summary>Live Jint engine; bindings use it to build native payloads and expose types.</summary>
    protected Jint.Engine Engine => Context.Engine;

    /// <summary>
    /// Called once, after the engine exists, before any plugin API is built. Register native
    /// facade classes here; do not capture plugin identity (classes are engine-global, the caller
    /// is resolved per call).
    /// </summary>
    public virtual void Install(Types.JsTypeRegistry types)
    {
    }

    /// <summary>Drops per-plugin resources (registrations, pending calls) when a plugin unloads.</summary>
    public virtual void Release(string plugin)
    {
    }

    /// <summary>
    /// Called once per host tick before JS timers run. Bindings that sample host state belong here
    /// so cadence and main-thread timing stay host-owned.
    /// </summary>
    public virtual void Pump()
    {
    }

    public virtual void Dispose()
    {
    }

    protected void Log(string level, string message) => Context.Log(level, message);

    protected void LogInfo(string message) => Context.Log("info", message);

    protected void LogWarn(string message) => Context.Log("warn", message);

    protected void LogError(string message) => Context.Log("error", message);
}
