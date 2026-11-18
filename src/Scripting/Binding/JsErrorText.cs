using System;
using Acornima;
using Jint;
using Jint.Native.Object;
using Jint.Runtime;

namespace ValheimJS.Scripting.Binding;

/// <summary>
/// One formatting path for script errors: keep the useful message, then add Jint's typed source
/// location and JavaScript call stack. Binding wrappers and plugin unload paths all use this so
/// a plugin author sees the same coordinates wherever the failure surfaces.
/// </summary>
internal static class JsErrorText
{
    internal static string Describe(Exception exception)
    {
        var inner = exception;
        while (inner.InnerException != null)
        {
            inner = inner.InnerException;
        }

        var script = FindScriptError(exception);
        var text = script != null
            ? ErrorName(script) + ": " + script.Message
            : inner.GetType().Name + ": " + inner.Message;

        SourceLocation? location = null;
        string? stack = null;
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (location == null && JintException.TryGetJavaScriptLocation(current, out var found))
            {
                location = found;
            }

            if (stack == null && JintException.TryGetJavaScriptCallStack(current, out var callStack))
            {
                stack = callStack;
            }

            if (location != null && stack != null)
            {
                break;
            }
        }

        if (location is { } source && source.Start.Line > 0)
        {
            var mapped = SourceMapRegistry.Map(source);
            text += string.IsNullOrEmpty(mapped.SourceFile)
                ? $" (line {mapped.Start.Line}, column {mapped.Start.Column})"
                : $" ({mapped.SourceFile}:{mapped.Start.Line}:{mapped.Start.Column})";
        }

        if (!string.IsNullOrWhiteSpace(stack))
        {
            text += Environment.NewLine + stack;
        }

        return text;
    }

    private static JavaScriptException? FindScriptError(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is JavaScriptException script)
            {
                return script;
            }
        }

        return null;
    }

    private static string ErrorName(JavaScriptException exception)
    {
        if (exception.Error is ObjectInstance error)
        {
            var name = error.Get("name");
            if (name.IsString() && name.AsString().Length > 0)
            {
                return name.AsString();
            }
        }

        return "Error";
    }
}
