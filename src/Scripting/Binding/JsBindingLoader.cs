using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ValheimJS.Scripting.Binding;

/// <summary>
/// Discovers every <see cref="JsBindingAttribute"/> type in the plugin assembly, orders them by
/// phase then order, and invokes their static <c>Bind(JsBindingContext)</c>.
/// A failure disables only that module — the rest of the API still installs.
/// </summary>
public static class JsBindingLoader
{
    public static IReadOnlyDictionary<string, JsBindingFunction> BindAll(
        Assembly assembly,
        JsBindingContext context,
        Action<string, string> log)
    {
        var modules = assembly.GetTypes()
            .Select(type => (Type: type, Attribute: type.GetCustomAttribute<JsBindingAttribute>()))
            .Where(item => item.Attribute != null)
            .OrderBy(item => item.Attribute!.Phase)
            .ThenBy(item => item.Attribute!.Order)
            .ThenBy(item => item.Type.FullName, StringComparer.Ordinal)
            .ToList();

        foreach (var (type, attribute) in modules)
        {
            var bind = type.GetMethod(
                "Bind",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(JsBindingContext) },
                null);

            if (bind == null)
            {
                log("error", $"[JsBinding:{attribute!.Name}] {type.FullName} must declare static void Bind(JsBindingContext)");
                continue;
            }

            try
            {
                bind.Invoke(null, new object[] { context });
                log("debug", $"[JsBinding:{attribute!.Name}] installed from {type.FullName}");
            }
            catch (Exception exception)
            {
                var root = exception.InnerException ?? exception;
                log("error", $"[JsBinding:{attribute!.Name}] failed to bind: {root.GetType().Name}: {root.Message}");
            }
        }

        return context.Functions;
    }
}
