using System;
using System.Collections.Generic;
using System.Linq;

namespace ValheimJS.Scripting.Binding;

/// <summary>
/// Base for bindings whose registrations are owned by one plugin. Supplies the per-plugin map,
/// uniform lookup errors, and the release/dispose walk that every scoped binding used to
/// re-implement.
/// </summary>
public abstract class ScopedBindings<TKey, TValue> : JsBindings where TKey : notnull
{
    private readonly Dictionary<string, Dictionary<TKey, TValue>> _owners = new(StringComparer.Ordinal);

    protected ScopedBindings(JsBindingContext context) : base(context)
    {
    }

    /// <summary>Per-plugin registrations, created on first use.</summary>
    protected Dictionary<TKey, TValue> For(string plugin)
    {
        if (!_owners.TryGetValue(plugin, out var values))
        {
            values = new Dictionary<TKey, TValue>();
            _owners[plugin] = values;
        }

        return values;
    }

    protected bool TryGet(string plugin, TKey key, out TValue value)
    {
        value = default!;
        return _owners.TryGetValue(plugin, out var values) && values.TryGetValue(key, out value!);
    }

    /// <summary>Uniform missing-registration error for binding methods.</summary>
    protected TValue Require(string plugin, TKey key, string what)
    {
        if (TryGet(plugin, key, out var value))
        {
            return value;
        }

        throw new InvalidOperationException($"{what} {key} does not exist for plugin '{plugin}'.");
    }

    protected IReadOnlyDictionary<string, Dictionary<TKey, TValue>> Owners => _owners;

    /// <summary>Every registration across plugins.</summary>
    protected IEnumerable<TValue> Values =>
        _owners.Values.SelectMany(values => values.Values);

    /// <summary>Removes one registration, reporting whether the key existed for this plugin.</summary>
    protected bool Remove(string plugin, TKey key, out TValue value)
    {
        value = default!;
        return _owners.TryGetValue(plugin, out var values) && values.Remove(key, out value!);
    }

    /// <summary>True when any plugin currently has registrations.</summary>
    protected bool Any => _owners.Count > 0;

    /// <summary>Called once for each registration when its plugin is released; override for cleanup.</summary>
    protected virtual void OnRelease(TValue value)
    {
    }

    public override void Release(string plugin)
    {
        if (!_owners.Remove(plugin, out var values))
        {
            return;
        }

        foreach (var value in values.Values)
        {
            OnRelease(value);
        }
    }

    public override void Dispose()
    {
        foreach (var plugin in _owners.Keys.ToList())
        {
            Release(plugin);
        }

        _owners.Clear();
    }
}
