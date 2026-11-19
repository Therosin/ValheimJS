using System;
using System.Collections.Generic;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;

namespace ValheimJS.Scripting.Binding;

/// <summary>
/// Converts the plain JS value graph used by bindings (objects, arrays, primitives) into CLR
/// dictionaries/lists and back. Bindings must use this instead of exposing CLR types to scripts.
/// </summary>
internal static class JsPlain
{
    /// <summary>Reads a JS value into plain CLR values. Unsupported values are rejected.</summary>
    internal static object? From(JsValue value)
    {
        if (value.IsNull() || value.IsUndefined())
        {
            return null;
        }

        if (value.IsString())
        {
            return value.AsString();
        }

        if (value.IsBoolean())
        {
            return value.AsBoolean();
        }

        if (value.IsNumber())
        {
            return value.AsNumber();
        }

        if (value.IsArray())
        {
            var array = value.AsArray();
            var length = (int)array.Length;
            var list = new List<object?>(length);
            for (var index = 0; index < length; index++)
            {
                list.Add(From(array.Get(index)));
            }

            return list;
        }

        if (value is ObjectInstance obj)
        {
            var map = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var key in obj.GetOwnPropertyKeys(Jint.Runtime.Types.String))
            {
                var name = key.ToString();
                map[name] = From(obj.Get(name));
            }

            return map;
        }

        throw new ArgumentException($"a JS value of type '{value.Type}' cannot cross into CLR state.");
    }

    /// <summary>Writes a plain CLR graph into the engine without exposing a CLR type.</summary>
    internal static JsValue FromPlain(Engine engine, object? value) => JsPayload.FromPlain(engine, value);

    /// <summary>Writes a value that may already be a <see cref="JsValue"/>; unsupported types are rejected.</summary>
    internal static JsValue ToJs(Engine engine, object? value)
    {
        if (value is JsValue js)
        {
            return js;
        }

        if (value is null or string or bool or double or float or long or int or short or byte)
        {
            return JsPayload.From(engine, value);
        }

        if (value is IReadOnlyDictionary<string, object?> readOnly)
        {
            return FromPlain(engine, readOnly);
        }

        if (value is IDictionary<string, object?> dictionary)
        {
            return FromPlain(engine, dictionary);
        }

        if (value is IEnumerable<object?> items)
        {
            return FromPlain(engine, items);
        }

        throw new ArgumentException($"value of type '{value.GetType().Name}' cannot cross into JS.");
    }
}
