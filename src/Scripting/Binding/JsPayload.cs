using System;
using System.Collections.Generic;
using System.Linq;
using Jint;
using Jint.Native;

namespace ValheimJS.Scripting.Binding;

/// <summary>
/// Builds JavaScript values natively instead of hand-writing JSON text. Bindings that return
/// structured data should return a <see cref="JsValue"/> with
/// <see cref="JsReturnKind.Native"/>; the bridge passes it through untouched.
/// <para>
/// No CLR type ever reaches script: objects are plain Jint <c>JsObject</c> instances and arrays
/// are plain <c>JsArray</c> instances, so plugin code cannot reflect on host types.
/// </para>
/// </summary>
public static class JsPayload
{
    /// <summary>A new object literal builder.</summary>
    public static JsObjectBuilder Object(Engine engine) => new(engine);

    /// <summary>A JS array from already-built values; null items become <c>null</c>.</summary>
    public static JsValue Array(Engine engine, IEnumerable<JsValue?> items)
    {
        var values = new List<JsValue>();
        foreach (var item in items)
        {
            values.Add(item ?? JsValue.Null);
        }

        return new JsArray(engine, values.ToArray());
    }

    /// <summary>A JS array from primitives (strings, numbers, booleans).</summary>
    public static JsValue Primitives(Engine engine, IEnumerable<object?> items)
    {
        var values = new List<JsValue>();
        foreach (var item in items)
        {
            values.Add(From(engine, item));
        }

        return new JsArray(engine, values.ToArray());
    }

    /// <summary>
    /// Converts a plain CLR graph (dictionaries, lists and primitives) into JS objects/arrays.
    /// Use for structured payloads produced by native services; no CLR type reaches script.
    /// </summary>
    public static JsValue FromPlain(Engine engine, object? value)
    {
        if (value == null)
        {
            return JsValue.Null;
        }

        if (value is JsValue jsValue)
        {
            return jsValue;
        }

        if (value is string text)
        {
            return JsValue.FromObject(engine, text);
        }

        if (value is bool flag)
        {
            return JsValue.FromObject(engine, flag);
        }

        if (value is double number)
        {
            return JsValue.FromObject(engine, number);
        }

        if (value is float single)
        {
            return JsValue.FromObject(engine, (double)single);
        }

        if (value is long integer)
        {
            return JsValue.FromObject(engine, (double)integer);
        }

        if (value is int small)
        {
            return JsValue.FromObject(engine, (double)small);
        }

        if (value is IReadOnlyDictionary<string, object?> readOnly)
        {
            return FromPlainDictionary(engine, readOnly);
        }

        if (value is IDictionary<string, object?> dictionary)
        {
            return FromPlainDictionary(engine, dictionary);
        }

        if (value is IEnumerable<object?> items)
        {
            var values = new List<JsValue>();
            foreach (var item in items)
            {
                values.Add(FromPlain(engine, item));
            }

            return new JsArray(engine, values.ToArray());
        }

        return From(engine, value);
    }

    private static JsValue FromPlainDictionary(Engine engine, IEnumerable<KeyValuePair<string, object?>> entries)
    {
        var builder = new JsObjectBuilder(engine);
        foreach (var pair in entries)
        {
            builder.Set(pair.Key, FromPlain(engine, pair.Value));
        }

        return builder.Build();
    }

    /// <summary>Converts a primitive to a JS value; null becomes <c>null</c>.</summary>
    public static JsValue From(Engine engine, object? value)
    {
        if (value == null)
        {
            return JsValue.Null;
        }

        if (value is JsValue jsValue)
        {
            return jsValue;
        }

        if (value is float single)
        {
            return JsValue.FromObject(engine, (double)single);
        }

        if (value is double or int or long or short or byte or bool or string)
        {
            return JsValue.FromObject(engine, value);
        }

        if (value is IEnumerable<string> strings)
        {
            return Array(engine, strings.Select(item => JsValue.FromObject(engine, item))!);
        }

        throw new ArgumentException($"npc/binding payload: unsupported value type '{value.GetType().Name}'; use JsPayload/JsValue.", nameof(value));
    }
}

/// <summary>Accumulates named fields into one JS object.</summary>
public sealed class JsObjectBuilder
{
    private readonly Engine _engine;
    private readonly List<KeyValuePair<string, JsValue>> _entries = new();

    internal JsObjectBuilder(Engine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    public int Count => _entries.Count;

    public JsObjectBuilder Set(string name, string? value) => Add(name, JsPayload.From(_engine, value));

    public JsObjectBuilder Set(string name, bool value) => Add(name, JsPayload.From(_engine, value));

    public JsObjectBuilder Set(string name, double value) => Add(name, JsPayload.From(_engine, value));

    public JsObjectBuilder Set(string name, float value) => Add(name, JsPayload.From(_engine, value));

    public JsObjectBuilder Set(string name, int value) => Add(name, JsPayload.From(_engine, value));

    public JsObjectBuilder Set(string name, long value) => Add(name, JsPayload.From(_engine, value));

    public JsObjectBuilder Set(string name, JsValue value) => Add(name, value ?? JsValue.Null);

    public JsObjectBuilder Set(string name, JsObjectBuilder value) => Add(name, value?.Build() ?? JsValue.Null);

    public JsObjectBuilder SetNull(string name) => Add(name, JsValue.Null);

    public JsObjectBuilder Set(string name, IEnumerable<JsValue?> items) => Add(name, JsPayload.Array(_engine, items));

    public JsValue Build() => JsObject.CreateFromEntries(_engine, _entries);

    private JsObjectBuilder Add(string name, JsValue value)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("payload field name is required", nameof(name));
        }

        _entries.Add(new KeyValuePair<string, JsValue>(name, value));
        return this;
    }
}
