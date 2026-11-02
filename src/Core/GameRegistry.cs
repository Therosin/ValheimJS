using System;
using System.Collections.Generic;
using UnityEngine;

namespace ValheimJS.Core;

/// <summary>
/// Typed registry of live game singletons, populated by Harmony patches once their
/// readiness predicate passes. Patches stay dumb capture points; contexts and hooks read
/// from here instead of re-resolving or holding their own copies.
/// Ported in spirit from LostMoon's GameRegistry.
/// </summary>
public static class GameRegistry
{
    private static readonly object Gate = new();
    private static readonly Dictionary<Type, object> Entries = new();

    public static void Register<T>(T instance) where T : class
    {
        if (instance == null)
        {
            return;
        }

        lock (Gate)
        {
            Entries[typeof(T)] = instance;
        }
    }

    /// <summary>Registers only when the predicate passes (never publish a half-initialized object).</summary>
    public static bool TryRegister<T>(T instance, Func<T, bool>? ready = null) where T : class
    {
        if (instance == null || (ready != null && !ready(instance)))
        {
            return false;
        }

        Register(instance);
        return true;
    }

    public static bool TryGet<T>(out T instance) where T : class
    {
        lock (Gate)
        {
            if (Entries.TryGetValue(typeof(T), out var value) && value is T typed && IsAlive(typed))
            {
                instance = typed;
                return true;
            }

            Entries.Remove(typeof(T));
        }

        instance = null!;
        return false;
    }

    public static void Clear()
    {
        lock (Gate)
        {
            Entries.Clear();
        }
    }

    private static bool IsAlive(object instance) => instance is not UnityEngine.Object unityObject || unityObject != null;
}
