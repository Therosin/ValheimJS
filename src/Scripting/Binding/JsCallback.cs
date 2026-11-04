using System;
using System.Collections.Generic;
using Jint;
using Jint.Native;
using Jint.Native.Function;

namespace ValheimJS.Scripting.Binding;

/// <summary>
/// A JavaScript function handed to a binding as a callback. The bridge creates these so a
/// binding never needs to know how Jint stores a function; invocation, JSON conversion and
/// error reporting stay in one place.
/// </summary>
public sealed class JsCallback
{
    private readonly Engine _engine;
    private readonly Function _function;
    private readonly string _description;

    internal JsCallback(Engine engine, Function function, string description)
    {
        _engine = engine;
        _function = function;
        _description = description;
    }

    internal Engine Engine => _engine;

    /// <summary>Where the callback came from, for diagnostics: <c>db.find(predicate)</c>.</summary>
    public string Description => _description;

    /// <summary>Calls the function with CLR arguments and returns the raw JavaScript result.</summary>
    public JsValue Invoke(params object?[] arguments)
    {
        var values = new JsValue[arguments.Length];
        for (var index = 0; index < arguments.Length; index++)
        {
            values[index] = JsValue.FromObject(_engine, arguments[index]);
        }

        try
        {
            return _function.Call(JsValue.Undefined, values);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"{_description} threw: {exception.GetType().Name}: {exception.Message}", exception);
        }
    }

    public bool InvokeBoolean(params object?[] arguments) => Invoke(arguments).AsBoolean();

    public string? InvokeString(params object?[] arguments)
    {
        var value = Invoke(arguments);
        return value.IsNull() || value.IsUndefined() ? null : value.ToString();
    }

    /// <summary>Parses JSON text into a JavaScript value (objects, arrays, primitives).</summary>
    public JsValue Parse(string json) => Json.Parse(_engine, json);

    /// <summary>Serialises a JavaScript value back to JSON text.</summary>
    public string Stringify(JsValue value) => Json.Stringify(_engine, value);

    /// <summary>Reads a string or numeric `id` from a JavaScript object, or null when invalid.</summary>
    public string? GetPropertyId(JsValue value)
    {
        if (value is not Jint.Native.Object.ObjectInstance instance)
        {
            return null;
        }

        var id = instance.Get("id");
        if (id.IsString())
        {
            var text = id.AsString();
            return text.Length == 0 ? null : text;
        }

        if (id.IsNumber())
        {
            return id.AsNumber().ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return null;
    }
}

/// <summary>
/// Cached access to the engine's own <c>JSON</c> object. Using the script-provided
/// implementation keeps parsing consistent with what plugins see, and avoids a second JSON
/// implementation in C#.
/// </summary>
internal static class Json
{
    public static JsValue Parse(Engine engine, string text) =>
        FunctionsFor(engine).Parse.Call(JsValue.Undefined, new JsValue[] { text });

    public static string Stringify(Engine engine, JsValue value)
    {
        var result = FunctionsFor(engine).Stringify.Call(JsValue.Undefined, new JsValue[] { value });
        return result.IsUndefined() ? "null" : result.ToString();
    }

    private sealed class Holder
    {
        public Holder(Engine engine)
        {
            var json = engine.GetValue("JSON").AsObject();
            Parse = json.Get("parse").As<Function>() ?? throw new InvalidOperationException("JSON.parse is missing from the engine.");
            Stringify = json.Get("stringify").As<Function>() ?? throw new InvalidOperationException("JSON.stringify is missing from the engine.");
        }

        public Function Parse { get; }

        public Function Stringify { get; }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Engine, Holder> Cache = new();

    private static Holder FunctionsFor(Engine engine) => Cache.GetValue(engine, static value => new Holder(value));
}
