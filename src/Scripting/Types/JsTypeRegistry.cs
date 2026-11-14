using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Interop;
using ValheimJS.Scripting.Binding;

namespace ValheimJS.Scripting.Types;

/// <summary>
/// Engine-owned registry of native facade classes exposed to scripts. One C# class
/// (<c>ValheimJS.BaseNPC</c>) becomes one JavaScript constructor (<c>ValheimBaseNPC</c>) with a
/// prototype and native-backed instances.
/// <para>
/// Safety rules: reflection happens here, once, at registration time; members are compiled into
/// <see cref="ClrFunction"/>s and property descriptors. No CLR object or <c>Type</c> is reachable
/// from script. The calling plugin is resolved per call from <c>__vjs_context</c>, never captured.
/// </para>
/// </summary>
public sealed class JsTypeRegistry
{
    private readonly Engine _engine;
    private readonly Dictionary<string, JsNativeType> _types = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PluginInstances> _instances = new(StringComparer.Ordinal);

    public JsTypeRegistry(Engine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    internal Engine Engine => _engine;

    /// <summary>Registers the class under the name from <see cref="JsTypeAttribute"/>.</summary>
    public JsNativeType Register<T>() where T : class
    {
        var attribute = typeof(T).GetCustomAttribute<JsTypeAttribute>();
        if (attribute == null)
        {
            throw new InvalidOperationException($"{typeof(T).FullName}: missing [JsType].");
        }

        return Register<T>(attribute.Name, attribute.Description, attributed: true, construct: null);
    }

    /// <summary>Registers the class under an explicit JavaScript name.</summary>
    public JsNativeType Register<T>(string name) where T : class =>
        Register<T>(name, null, attributed: false, construct: null);

    /// <summary>Registers an attributed class with a host-owned constructor (opaque handle in).</summary>
    public JsNativeType Register<T>(Func<JsValue[], object?> construct) where T : class
    {
        var attribute = typeof(T).GetCustomAttribute<JsTypeAttribute>();
        if (attribute == null)
        {
            throw new InvalidOperationException($"{typeof(T).FullName}: missing [JsType].");
        }

        return Register<T>(attribute.Name, attribute.Description, attributed: true, construct);
    }

    /// <summary>Builds a class explicitly; the builder wins over attribute-driven discovery.</summary>
    public JsClassBuilder Define<T>(string name) where T : class
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A JavaScript type name is required.", nameof(name));
        }

        return new JsClassBuilder(this, name, typeof(T), null);
    }

    public JsNativeType? Find(string name) => _types.TryGetValue(name, out var type) ? type : null;

    /// <summary>Importable type names, for diagnostics/declarations.</summary>
    public IReadOnlyList<JsNativeType> Types => _types.Values.OrderBy(type => type.Name, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Returns this plugin's JavaScript object for a native handle, creating it on first use.
    /// Identity is stable for the plugin: the same handle yields the same object.
    /// </summary>
    public ObjectInstance Instance(JsNativeType type, object handle)
    {
        if (handle == null)
        {
            throw new ArgumentNullException(nameof(handle));
        }

        var cache = ScopeFor(CurrentPlugin);
        if (cache.NativeToJs.TryGetValue(handle, out var value))
        {
            return value;
        }

        return CreateInstance(type, handle, type.Prototype);
    }

    /// <summary>Recovers the native object behind a JavaScript instance.</summary>
    public object? Handle(JsValue value)
    {
        if (value is not ObjectInstance instance)
        {
            return null;
        }

        return ScopeFor(CurrentPlugin).JsToNative.TryGetValue(instance, out var handle) ? handle : null;
    }

    /// <summary>Typed handle accessor for member implementations.</summary>
    public T? Handle<T>(JsValue value) where T : class => Handle(value) as T;

    /// <summary>Drops one plugin's instance cache; classes stay registered for the engine.</summary>
    public void Release(string plugin) => _instances.Remove(plugin);

    /// <summary>Drops every plugin cache; classes stay registered.</summary>
    public void Clear() => _instances.Clear();

    /// <summary>Calling plugin for the current script execution.</summary>
    public string CurrentPlugin
    {
        get
        {
            if (_engine.GetValue("__vjs_context") is ObjectInstance context && context.Get("name").IsString())
            {
                return context.Get("name").AsString();
            }

            return "host";
        }
    }

    internal JsNativeType Define(JsClassBuilder builder)
    {
        var type = new JsNativeType(builder.Name, builder.ClrType, builder.Description);
        var constructor = new JsNativeConstructor(this, type, builder.Constructor);
        type.Constructor = constructor;
        type.Prototype = new JsObject(_engine) { Prototype = _engine.Intrinsics.Object.PrototypeObject };
        constructor.SetPrototypeProperty(type.Prototype);
        type.Prototype.FastSetProperty(
            "constructor",
            new PropertyDescriptor(constructor, PropertyFlag.Configurable));

        var prototype = type.Prototype;
        foreach (var member in builder.Members)
        {
            prototype.FastSetProperty(member.Name, member.Descriptor);
        }

        _types[type.Name] = type;
        return type;
    }

    internal ObjectInstance CreateInstance(JsNativeType type, object handle, ObjectInstance prototype)
    {
        var instance = new JsObject(_engine) { Prototype = prototype };
        var cache = ScopeFor(CurrentPlugin);
        cache.JsToNative.Add(instance, handle);
        cache.NativeToJs.Add(handle, instance);
        return instance;
    }

    private PluginInstances ScopeFor(string plugin)
    {
        if (!_instances.TryGetValue(plugin, out var cache))
        {
            cache = new PluginInstances();
            _instances[plugin] = cache;
        }

        return cache;
    }

    private JsNativeType Register<T>(string name, string? description, bool attributed, Func<JsValue[], object?>? construct) where T : class
    {
        var builder = new JsClassBuilder(this, name, typeof(T), description);
        if (construct != null)
        {
            builder.Construct(construct);
        }

        if (attributed)
        {
            DiscoverMembers(builder, typeof(T));
        }

        return Define(builder);
    }

    private static void DiscoverMembers(JsClassBuilder builder, Type type)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.IsDefined(typeof(JsIgnoreAttribute), false) || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            if (!JsClassBuilder.SupportsType(property.PropertyType))
            {
                continue;
            }

            var name = property.GetCustomAttribute<JsNameAttribute>()?.Name ?? DefaultName(property.Name);
            builder.ExposeProperty(name, property);
        }

        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (method.IsDefined(typeof(JsIgnoreAttribute), false) || method.IsSpecialName ||
                method.DeclaringType == typeof(object))
            {
                continue;
            }

            if (!JsClassBuilder.HasSupportedSignature(method, out _))
            {
                continue;
            }

            var name = method.GetCustomAttribute<JsNameAttribute>()?.Name ?? DefaultName(method.Name);
            builder.ExposeMethod(name, method);
        }
    }

    private static string DefaultName(string name) =>
        name.Length > 0 && char.IsUpper(name[0]) ? char.ToLowerInvariant(name[0]) + name.Substring(1) : name;

    private sealed class PluginInstances
    {
        internal readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, ObjectInstance> NativeToJs = new();
        internal readonly System.Runtime.CompilerServices.ConditionalWeakTable<ObjectInstance, object> JsToNative = new();
    }
}

/// <summary>One registered JavaScript class.</summary>
public sealed class JsNativeType
{
    internal JsNativeType(string name, Type clrType, string? description)
    {
        Name = name;
        ClrType = clrType;
        Description = description;
    }

    public string Name { get; }

    public Type ClrType { get; }

    public string? Description { get; }

    public Constructor Constructor { get; internal set; } = null!;

    /// <summary>Prototype object; shared by instances of this class (and subclasses via newTarget).</summary>
    public ObjectInstance Prototype { get; internal set; } = null!;
}

/// <summary>Native constructor: validates/handles the native object, then creates the JS instance.</summary>
internal sealed class JsNativeConstructor : Constructor
{
    private readonly JsTypeRegistry _registry;
    private readonly JsNativeType _type;
    private readonly Func<JsValue[], object?>? _construct;

    internal JsNativeConstructor(JsTypeRegistry registry, JsNativeType type, Func<JsValue[], object?>? construct)
        : base(registry.Engine, type.Name)
    {
        _registry = registry;
        _type = type;
        _construct = construct;
    }

    internal void SetPrototypeProperty(ObjectInstance prototype) =>
        SetOwnProperty(
            JsValue.FromObject(_registry.Engine, "prototype"),
            new PropertyDescriptor(prototype, PropertyFlag.AllForbidden | PropertyFlag.Writable));

    public override ObjectInstance Construct(JsValue[] arguments, JsValue newTarget)
    {
        if (_construct == null)
        {
            throw new InvalidOperationException($"{_type.Name} cannot be constructed from script.");
        }

        var handle = _construct(arguments);
        if (handle == null)
        {
            throw new InvalidOperationException($"{_type.Name}: constructor did not return a native handle.");
        }

        var prototype = _type.Prototype;
        if (newTarget is ObjectInstance target)
        {
            var declared = target.Get("prototype");
            if (declared is ObjectInstance candidate)
            {
                prototype = candidate;
            }
        }

        return _registry.CreateInstance(_type, handle, prototype);
    }
}

/// <summary>Accumulates members for one class definition.</summary>
public sealed class JsClassBuilder
{
    internal JsClassBuilder(JsTypeRegistry registry, string name)
        : this(registry, name, null, null)
    {
    }

    internal JsClassBuilder(JsTypeRegistry registry, string name, Type? clrType, string? description)
    {
        Registry = registry;
        Name = name;
        ClrType = clrType ?? typeof(object);
        Description = description;
    }

    internal JsTypeRegistry Registry { get; }

    internal string Name { get; }

    internal Type ClrType { get; }

    internal string? Description { get; }

    internal Func<JsValue[], object?>? Constructor { get; private set; }

    internal List<JsMember> Members { get; } = new();

    public JsNativeType Build() => Registry.Define(this);

    public JsClassBuilder Construct(Func<JsValue[], object?> create)
    {
        Constructor = create ?? throw new ArgumentNullException(nameof(create));
        return this;
    }

    public JsClassBuilder Getter(string name, Func<object, object?> get)
    {
        var engine = Registry.Engine;
        var getter = new ClrFunction(engine, name, (thisValue, _) =>
        {
            var target = Registry.Handle(thisValue) ?? throw new InvalidOperationException($"{name}: not a {Name}.");
            return JsPayload.From(engine, get(target));
        });
        Members.Add(new JsMember(name, new GetSetPropertyDescriptor(getter, null, enumerable: true, configurable: true)));
        return this;
    }

    public JsClassBuilder Setter(string name, Action<object, JsValue> set)
    {
        var engine = Registry.Engine;
        var setter = new ClrFunction(engine, name, (thisValue, args) =>
        {
            var target = Registry.Handle(thisValue) ?? throw new InvalidOperationException($"{name}: not a {Name}.");
            set(target, args.Length > 0 ? args[0] : JsValue.Undefined);
            return JsValue.Undefined;
        });
        Members.Add(new JsMember(name, new GetSetPropertyDescriptor(null, setter, enumerable: true, configurable: true)));
        return this;
    }

    public JsClassBuilder Method(string name, Func<object, JsValue[], JsValue> call)
    {
        var engine = Registry.Engine;
        var method = new ClrFunction(engine, name, (thisValue, args) =>
        {
            var target = Registry.Handle(thisValue) ?? throw new InvalidOperationException($"{name}: not a {Name}.");
            return call(target, args);
        });
        Members.Add(new JsMember(name, new PropertyDescriptor(method, PropertyFlag.Configurable | PropertyFlag.Writable)));
        return this;
    }

    internal void ExposeProperty(string name, PropertyInfo property)
    {
        var engine = Registry.Engine;
        var read = property.CanRead
            ? new ClrFunction(engine, name, (thisValue, _) =>
            {
                var target = Registry.Handle(thisValue) ?? throw new InvalidOperationException($"{name}: not a {Name}.");
                return JsPayload.From(engine, property.GetValue(target));
            })
            : null;
        var write = property.CanWrite
            ? new ClrFunction(engine, name, (thisValue, args) =>
            {
                var target = Registry.Handle(thisValue) ?? throw new InvalidOperationException($"{name}: not a {Name}.");
                property.SetValue(target, args.Length > 0 ? args[0].ToObject() : null);
                return JsValue.Undefined;
            })
            : null;
        Members.Add(new JsMember(name, new GetSetPropertyDescriptor(read, write, enumerable: true, configurable: true)));
    }

    internal void ExposeMethod(string name, MethodInfo method)
    {
        var engine = Registry.Engine;
        var parameters = method.GetParameters();
        var function = new ClrFunction(engine, name, (thisValue, args) =>
        {
            var target = Registry.Handle(thisValue) ?? throw new InvalidOperationException($"{name}: not a {Name}.");
            var converted = new object?[parameters.Length];
            for (var index = 0; index < parameters.Length; index++)
            {
                var parameter = parameters[index];
                var value = index < args.Length ? args[index] : JsValue.Undefined;
                if (index >= args.Length && parameter.HasDefaultValue)
                {
                    converted[index] = parameter.DefaultValue;
                    continue;
                }

                converted[index] = parameter.ParameterType == typeof(JsValue)
                    ? value
                    : value.ToObject();
                if (converted[index] is double number && parameter.ParameterType != typeof(double))
                {
                    converted[index] = System.Convert.ChangeType(number, parameter.ParameterType);
                }
            }

            var result = method.Invoke(target, converted);
            return result is JsValue value2 ? value2 : JsPayload.From(engine, result);
        }, parameters.Length);
        Members.Add(new JsMember(name, new PropertyDescriptor(function, PropertyFlag.Configurable | PropertyFlag.Writable)));
    }

    internal static bool SupportsType(Type type)
    {
        var name = TypeName(type);
        if (type.IsArray && type.GetArrayRank() == 1)
        {
            return TypeName(type.GetElementType()!) == "System.String";
        }

        return name == "System.String" || name == "Jint.Native.JsValue" || (Nullable.GetUnderlyingType(type) ?? type).IsPrimitive;
    }

    internal static string TypeName(Type type)
    {
        var narrowed = Nullable.GetUnderlyingType(type) ?? type;
        return narrowed.FullName ?? narrowed.Name;
    }

    internal static bool HasSupportedSignature(MethodInfo method, out string reason)
    {
        reason = string.Empty;
        foreach (var parameter in method.GetParameters())
        {
            var name = TypeName(parameter.ParameterType);
            if (name == "System.String" || name == "Jint.Native.JsValue" || (Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType).IsPrimitive)
            {
                continue;
            }

            reason = $"parameter '{parameter.Name}' of type '{parameter.ParameterType.Name}' is not supported";
            return false;
        }

        var returnName = TypeName(method.ReturnType);
        if (returnName == "System.Void" || returnName == "Jint.Native.JsValue" ||
            returnName == "System.String" || SupportsType(method.ReturnType) ||
            (Nullable.GetUnderlyingType(method.ReturnType) ?? method.ReturnType).IsPrimitive)
        {
            return true;
        }

        reason = $"return type '{method.ReturnType.Name}' is not supported";
        return false;
    }

    internal sealed class JsMember
    {
        internal JsMember(string name, PropertyDescriptor descriptor)
        {
            Name = name;
            Descriptor = descriptor;
        }

        internal string Name { get; }

        internal PropertyDescriptor Descriptor { get; }
    }
}
