using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Jint;
using Jint.Native;
using Jint.Native.Function;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Interop;
using Jint.Native.Object;

namespace ValheimJS.Scripting.Binding;

/// <summary>
/// Turns a validated <see cref="JsBindingCatalog"/> into JS objects and invokes attributed
/// methods through one controlled bridge: argument conversion, plugin scoping, JSON returns and
/// error reporting all happen here. No CLR type is ever exposed to script code.
/// </summary>
public sealed class JsBindingInstaller : IDisposable
{
    private readonly JsBindingContext _context;
    private readonly List<(JsBinderMetadata Binder, JsBindings Instance)> _instances = new();
    private readonly List<(JsBinderMetadata Binder, JsMethodMetadata Method)> _methods = new();
    private bool _typesReady;

    public JsBindingInstaller(JsBindingCatalog catalog, JsBindingContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

        if (!catalog.IsValid)
        {
            throw new InvalidOperationException("binding catalog is invalid:" + Environment.NewLine + catalog.DescribeErrors());
        }

        foreach (var binder in catalog.Binders)
        {
            if (!Matches(context, binder.Availability)) continue;
            var constructor = binder.Type.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(JsBindingContext) },
                null);

            if (constructor == null)
            {
                throw new InvalidOperationException(
                    $"[JsBinder:{binder.Namespace}] {binder.Type.FullName}: constructor accepting JsBindingContext disappeared after discovery.");
            }

            var instance = (JsBindings)constructor.Invoke(new object[] { context });
            _instances.Add((binder, instance));
            _context.Own(instance);

            foreach (var method in binder.Methods)
            {
                _methods.Add((binder, method));
            }
        }
    }

    public JsBindingCatalog Catalog { get; }

    /// <summary>Names of every binder that installed on this process.</summary>
    public IReadOnlyList<string> InstalledNamespaces => Catalog.Binders
        .Where(binder => Matches(_context, binder.Availability))
        .Select(binder => binder.Namespace)
        .ToList();

    public int InstalledMethodCount => _methods.Count(item =>
        Matches(_context, item.Binder.Availability) && Matches(_context, item.Method.Availability));

    /// <summary>
    /// Builds this plugin's view of <c>host.api</c>. Scoped binders are duplicated per plugin so
    /// the injected name is a closure, never a mutable "current plugin" global.
    /// </summary>
    public JsValue BuildApi(Engine engine, string pluginName)
    {
        var root = new JsObject(engine);

        if (!_typesReady)
        {
            _typesReady = true;
            var types = _context.TypesOrNull;
            if (types != null)
            {
                foreach (var (_, instance) in _instances)
                {
                    instance.Install(types);
                }
            }
        }

        foreach (var (binder, instance) in _instances)
        {
            if (!Matches(_context, binder.Availability))
            {
                continue;
            }

            var ns = new JsObject(engine);
            var any = false;

            foreach (var method in binder.Methods)
            {
                if (!Matches(_context, binder.Availability) || !Matches(_context, method.Availability))
                {
                    continue;
                }

                ns.Set(method.Name, CreateFunction(engine, binder, method, instance, pluginName), throwOnError: false);
                any = true;
            }

            if (any)
            {
                root.Set(binder.Namespace, ns, throwOnError: false);
            }
        }

        return root;
    }

    /// <summary>Releases per-plugin registrations; called when a plugin unloads.</summary>
    public void ReleasePlugin(string plugin) => _context.ReleasePlugin(plugin);

    public void Dispose()
    {
        _instances.Clear();
        _methods.Clear();
        _context.Dispose();
    }

    /// <summary>One callable, with conversion in and out. Everything script code can reach passes here.</summary>
    private Function CreateFunction(
        Engine engine,
        JsBinderMetadata binder,
        JsMethodMetadata method,
        JsBindings instance,
        string pluginName)
    {
        var name = binder.Namespace + "." + method.Name;
        var parameters = method.Parameters;
        var invoker = method.Method;

        return new ClrFunction(engine, name, (_, arguments) =>
        {
            try
            {
                var values = new object?[parameters.Count + (method.Scoped ? 1 : 0)];
                var offset = 0;

                if (method.Scoped)
                {
                    values[offset++] = pluginName;
                }

                for (var index = 0; index < parameters.Count; index++)
                {
                    var parameter = parameters[index];
                    var argument = index < arguments.Length ? arguments[index] : JsValue.Undefined;
                    values[offset + index] = ConvertArgument(engine, argument, parameter, name);
                }

                var result = invoker.Invoke(instance, values);
                return ToScript(engine, result, method, name);
            }
            catch (TargetInvocationException exception) when (exception.InnerException != null)
            {
                throw new InvalidOperationException($"{name}: {exception.InnerException.Message}", exception.InnerException);
            }
        }, Math.Max(0, parameters.Count));
    }

    private static object? ConvertArgument(Engine engine, JsValue value, JsParameterMetadata parameter, string where)
    {
        var type = parameter.Type;
        var label = $"{where}({parameter.Name})";

        if (type.Kind == JsTypeKind.Callback)
        {
            if (value is not Function function)
            {
                throw new ArgumentException($"{label} expects a function.");
            }

            return new JsCallback(engine, function, label);
        }

        if (type.Kind == JsTypeKind.Unknown)
        {
            return value.ToObject();
        }

        if (type.Kind == JsTypeKind.Array)
        {
            if (!value.IsArray())
            {
                throw new ArgumentException($"{label} expects an array.");
            }

            var array = value.AsArray();
            var element = type.Element!;
            var result = new string[array.Length];
            for (var index = 0; index < array.Length; index++)
            {
                result[index] = ConvertElement(array.Get(index), element, $"{label}[{index}]") as string ?? string.Empty;
            }

            return result;
        }

        if (value.IsNull() || value.IsUndefined())
        {
            if (type.Kind == JsTypeKind.String)
            {
                return null;
            }

            throw new ArgumentException($"{label} is required.");
        }

        return type.Kind switch
        {
            JsTypeKind.String => value.IsString()
                ? value.AsString()
                : throw new ArgumentException($"{label} expects a string."),
            JsTypeKind.Boolean => value.IsBoolean()
                ? value.AsBoolean()
                : throw new ArgumentException($"{label} expects a boolean."),
            JsTypeKind.Number => value.IsNumber()
                ? Coerce(value.AsNumber(), parameter.ClrType)
                : throw new ArgumentException($"{label} expects a number."),
            _ => throw new ArgumentException($"{label} has an unsupported type.")
        };
    }

    private static object Coerce(double number, Type target) =>
        target == typeof(double) ? number : System.Convert.ChangeType(number, target, CultureInfo.InvariantCulture);

    private static object? ConvertElement(JsValue value, JsTypeRef element, string label) => element.Kind switch
    {
        JsTypeKind.String => value.IsString() ? value.AsString() : throw new ArgumentException($"{label} expects a string."),
        JsTypeKind.Number => value.IsNumber() ? value.AsNumber() : throw new ArgumentException($"{label} expects a number."),
        JsTypeKind.Boolean => value.IsBoolean() ? value.AsBoolean() : throw new ArgumentException($"{label} expects a boolean."),
        _ => value.ToObject()
    };

    private static JsValue ToScript(Engine engine, object? result, JsMethodMetadata method, string where)
    {
        if (method.ReturnType.Kind == JsTypeKind.Void)
        {
            return JsValue.Undefined;
        }

        if (method.ReturnKind == JsReturnKind.Json)
        {
            if (result == null)
            {
                return JsValue.Null;
            }

            return Json.Parse(engine, System.Convert.ToString(result, CultureInfo.InvariantCulture)!);
        }

        return result == null ? JsValue.Null : JsValue.FromObject(engine, result);
    }

    private static bool Matches(JsBindingContext context, JsAvailability availability)
    {
        if (availability == JsAvailability.Always)
        {
            return true;
        }

        var current = context.Dedicated
            ? JsAvailability.Server | JsAvailability.Dedicated
            : JsAvailability.Client;

        return (availability & current) != 0;
    }


}
