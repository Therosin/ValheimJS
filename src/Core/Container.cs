using System;
using System.Collections.Generic;

namespace Core.Container
{
    /// <summary>
    /// Type-keyed registry for managed services and Unity objects. References are held
    /// until replaced or cleared; destroyed Unity objects are removed on lookup or cleanup.
    /// Call methods involving Unity objects from Unity's main thread.
    /// </summary>
    /// <remarks>
    /// Examples use hypothetical game service types supplied by the host. Capture from setup or update
    /// patches, resolve from consumers, and clear from disconnect or teardown hooks. Readiness gates
    /// control storage; they do not suppress the patched game's original method by themselves.
    /// </remarks>
    internal static class Container
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<Type, object> Entries = new Dictionary<Type, object>();

        /// <summary>Describes whether a readiness-gated capture stored a new instance, matched the existing one, or could not proceed.</summary>
        public enum RegisterResult
        {
            /// <summary>The candidate passed readiness and matches the live reference already stored under this type.</summary>
            AlreadyRegistered,
            /// <summary>The candidate passed readiness and was stored as a new or replacement reference.</summary>
            Registered,
            /// <summary>The candidate is null or destroyed, the predicate is null, or the readiness check failed.</summary>
            NotReady
        }

        /// <summary>Stores a live instance under its declared registration type, replacing that type's previous value.</summary>
        /// <typeparam name="T">The exact type key used later to resolve or clear the reference.</typeparam>
        /// <param name="instance">The managed service or live Unity object to retain.</param>
        /// <remarks>
        /// Null or destroyed candidates are rejected with an error and leave existing storage unchanged.
        /// References are held strongly until replaced, cleared, or removed after Unity destruction.
        /// Registering a concrete type does not also register its interfaces or base types.
        /// Use a readiness helper when a patch can observe an instance before initialization completes.
        /// </remarks>
        /// <example><code>
        /// // In a setup postfix, once the service is known to be ready:
        /// Container.Register&lt;IPlayerService&gt;(__instance);
        /// // Consumers must use the same type key:
        /// Container.TryGet&lt;IPlayerService&gt;(out var players);
        /// </code></example>
        public static void Register<T>(T instance) where T : class
        {
            if (!IsAlive(instance))
            {
                global::ValheimJS.Utils.Logger.Shared.LogError("Container", "Cannot register a null or destroyed " + typeof(T).Name + ".");
                return;
            }

            lock (Sync) { Entries[typeof(T)] = instance; }
        }

        /// <summary>Resolves a live reference by its exact registered type, logging an error if unavailable.</summary>
        /// <typeparam name="T">The type key supplied when registering the reference.</typeparam>
        /// <returns>The stored instance, or null if absent or destroyed.</returns>
        /// <remarks>Destroyed Unity entries are removed. Prefer TryGet for expected startup or per-frame availability checks.</remarks>
        /// <example><code>
        /// // After bootstrap has registered this dependency:
        /// var players = Container.Get&lt;PlayerService&gt;();
        /// if (players == null) return;
        /// var player = players.LocalPlayer;
        /// </code></example>
        public static T Get<T>() where T : class
        {
            T instance;
            if (TryGet(out instance)) return instance;
            global::ValheimJS.Utils.Logger.Shared.LogError("Container", "Reference " + typeof(T).Name + " not found.");
            return null;
        }

        /// <summary>Resolves a required live reference by the registered type's simple or full name.</summary>
        /// <param name="typeName">The case-sensitive type name, preferably its full namespace-qualified name.</param>
        /// <returns>The stored instance.</returns>
        /// <exception cref="InvalidOperationException">No live registration matches, including when the name is blank.</exception>
        /// <remarks>Use TryGet when a scripting or tooling caller should tolerate an unavailable service.</remarks>
        /// <example><code>
        /// // A tooling bridge resolving an already-registered, required service:
        /// object players = Container.Get(typeof(PlayerService).FullName);
        /// </code></example>
        public static object Get(string typeName)
        {
            object instance;
            if (TryGet(typeName, out instance)) return instance;
            throw new InvalidOperationException("Service \"" + typeName + "\" is not registered.");
        }

        /// <summary>Removes the reference stored under the specified type key.</summary>
        /// <typeparam name="T">The exact registered type to remove.</typeparam>
        /// <remarks>
        /// Does nothing if absent. Does not destroy or dispose the instance, clear other type keys,
        /// or invalidate references already cached by consumers. Call from disconnect or teardown hooks.
        /// </remarks>
        /// <example><code>
        /// // In a session-disconnected postfix:
        /// Container.Clear&lt;SessionService&gt;();
        /// </code></example>
        public static void Clear<T>() where T : class
        {
            lock (Sync) { Entries.Remove(typeof(T)); }
        }

        /// <summary>Releases every stored reference, for example during plugin shutdown or a full session reset.</summary>
        /// <remarks>Does not destroy or dispose stored objects, clear consumer caches, or reset lifecycle states and handlers.</remarks>
        /// <example><code>
        /// // After stopping code that consumes these registrations:
        /// Container.ClearAll();
        /// </code></example>
        public static void ClearAll()
        {
            lock (Sync) { Entries.Clear(); }
        }

        /// <summary>Resolves a live reference without logging when it is unavailable.</summary>
        /// <typeparam name="T">The exact type key used during registration.</typeparam>
        /// <param name="instance">Receives the stored reference on success, or null on failure.</param>
        /// <returns>True if a live reference is available; otherwise false.</returns>
        /// <remarks>
        /// Destroyed Unity entries are removed. This checks availability, not the original readiness predicate;
        /// a consumer that caches the result must still handle later destruction or session changes.
        /// </remarks>
        /// <example><code>
        /// // In an update or input handler, wait until patches capture both dependencies:
        /// if (!Container.TryGet&lt;InputService&gt;(out var input)) return;
        /// if (!Container.TryGet&lt;LocalPlayer&gt;(out var player)) return;
        /// // Use input and player for this frame.
        /// </code></example>
        public static bool TryGet<T>(out T instance) where T : class
        {
            lock (Sync)
            {
                object value;
                if (TryGetAlive(typeof(T), out value))
                {
                    instance = value as T;
                    return instance != null;
                }
                instance = null;
                return false;
            }
        }

        /// <summary>Resolves a live reference by its registered type's simple or full name without logging a miss.</summary>
        /// <param name="typeName">A case-sensitive type name; null, empty, or whitespace-only names fail.</param>
        /// <param name="instance">Receives the stored reference on success, or null on failure.</param>
        /// <returns>True if a matching live registration is found; otherwise false.</returns>
        /// <remarks>
        /// Use full names to distinguish types in different namespaces. Simple-name collisions have no
        /// specified winner. Lookup uses registered type keys, not the instance's runtime type, and removes
        /// matched destroyed Unity entries. Names are not trimmed before comparison.
        /// </remarks>
        /// <example><code>
        /// // A scripting bridge can wait for a captured service without producing errors:
        /// if (!Container.TryGet(typeof(PlayerService).FullName, out var service)) return;
        /// </code></example>
        public static bool TryGet(string typeName, out object instance)
        {
            instance = null;
            if (string.IsNullOrEmpty(typeName) || typeName.Trim().Length == 0) return false;
            lock (Sync)
            {
                Type match = null;
                foreach (var type in Entries.Keys)
                {
                    if (string.Equals(typeName, type.FullName, StringComparison.Ordinal) ||
                        string.Equals(typeName, type.Name, StringComparison.Ordinal))
                    {
                        match = type;
                        break;
                    }
                }
                return match != null && TryGetAlive(match, out instance);
            }
        }

        /// <summary>Checks whether any live reference is stored under the specified type key.</summary>
        /// <typeparam name="T">The exact registered type to check.</typeparam>
        /// <returns>True if a live reference is available; otherwise false.</returns>
        /// <remarks>Does not reevaluate readiness. Use the instance overload when a patch must detect a replacement object.</remarks>
        /// <example><code>
        /// // A connection callback capturing a service only when its slot is empty:
        /// if (!Container.IsRegistered&lt;SessionService&gt;())
        ///     Container.TryRegisterAndLog(__instance, session =&gt; session.IsConnected, "SessionPatch");
        /// </code></example>
        public static bool IsRegistered<T>() where T : class
        {
            T instance;
            return TryGet(out instance);
        }

        /// <summary>Checks whether this particular live instance is already stored under the specified type key.</summary>
        /// <typeparam name="T">The exact registered type to check.</typeparam>
        /// <param name="instance">The candidate observed by a patch or lifecycle callback.</param>
        /// <returns>True for the stored live instance; false for an absent, different, null, or destroyed candidate.</returns>
        /// <remarks>
        /// Managed services use reference identity; Unity objects use Unity equality. This does not reevaluate
        /// readiness, so an outer identity guard stops retrying the predicate once this instance is captured.
        /// </remarks>
        /// <example><code>
        /// // In an update postfix, retry until ready and capture replacement instances:
        /// if (!Container.IsRegistered&lt;PlayerService&gt;(__instance))
        ///     Container.TryRegisterAndLog(__instance, service =&gt; service.LocalPlayer != null, "PlayerPatch");
        /// </code></example>
        public static bool IsRegistered<T>(T instance) where T : class
        {
            if (!IsAlive(instance)) return false;
            lock (Sync)
            {
                object registered;
                return TryGetAlive(typeof(T), out registered) && SameInstance(registered, instance);
            }
        }

        /// <summary>Stores a candidate only when it is alive and its readiness predicate passes.</summary>
        /// <typeparam name="T">The exact type key under which to store the candidate.</typeparam>
        /// <param name="instance">The candidate service or Unity object.</param>
        /// <param name="isReady">A predicate evaluated for a live candidate; null means not ready.</param>
        /// <returns>
        /// Registered for a new or replacement instance, AlreadyRegistered for the same ready instance,
        /// or NotReady for a null/destroyed candidate, null predicate, or failed readiness check.
        /// </returns>
        /// <remarks>
        /// A failed gate leaves the previous registration unchanged. Readiness is checked on each call,
        /// including calls for an already stored instance; it is not monitored after registration.
        /// Predicate exceptions propagate to the caller. Call again from a later patch invocation to retry.
        /// </remarks>
        /// <example><code>
        /// var result = Container.AutoRegisterIfReadyEx(
        ///     __instance, service =&gt; service.LocalPlayer != null);
        /// if (result == Container.RegisterResult.Registered)
        /// {
        ///     // Perform capture-specific setup only for a newly stored instance.
        /// }
        /// </code></example>
        public static RegisterResult AutoRegisterIfReadyEx<T>(T instance, Func<T, bool> isReady) where T : class
        {
            if (!IsAlive(instance) || isReady == null || !isReady(instance) || !IsAlive(instance))
                return RegisterResult.NotReady;

            lock (Sync)
            {
                object registered;
                bool same = TryGetAlive(typeof(T), out registered) && SameInstance(registered, instance);
                Entries[typeof(T)] = instance;
                return same ? RegisterResult.AlreadyRegistered : RegisterResult.Registered;
            }
        }

        /// <summary>Conditionally stores a candidate and reports the outcome as a nullable Boolean.</summary>
        /// <typeparam name="T">The exact registration type key.</typeparam>
        /// <param name="instance">The candidate service or Unity object.</param>
        /// <param name="isReady">The readiness predicate; null means not ready.</param>
        /// <returns>True for a new registration, false for not ready, or null for an already registered ready instance.</returns>
        /// <remarks>Has the same readiness, replacement, and exception behavior as AutoRegisterIfReadyEx.</remarks>
        /// <example><code>
        /// bool? captured = Container.AutoRegisterIfReady(__instance, service =&gt; service.IsReady);
        /// if (captured == true)
        /// {
        ///     // Initialize consumers for this newly captured instance.
        /// }
        /// </code></example>
        public static bool? AutoRegisterIfReady<T>(T instance, Func<T, bool> isReady) where T : class
        {
            switch (AutoRegisterIfReadyEx(instance, isReady))
            {
                case RegisterResult.Registered: return true;
                case RegisterResult.AlreadyRegistered: return null;
                default: return false;
            }
        }

        /// <summary>Conditionally captures a reference and logs a successful registration or identity match.</summary>
        /// <typeparam name="T">The exact registration type key.</typeparam>
        /// <param name="instance">The service or Unity object observed by the caller.</param>
        /// <param name="isReady">The readiness predicate; null means not ready.</param>
        /// <param name="logSource">The patch or subsystem label included in messages.</param>
        /// <returns>The registration outcome from AutoRegisterIfReadyEx.</returns>
        /// <remarks>
        /// New registrations log at Info; identity matches log at Debug. NotReady is silent so a patch can
        /// retry every frame. Predicate exceptions propagate. Consumers resolve the captured value with TryGet.
        /// </remarks>
        /// <example><code>
        /// // In an update postfix, do not capture a service until its dependencies exist:
        /// if (!Container.IsRegistered&lt;PlayerService&gt;(__instance))
        ///     Container.TryRegisterAndLog(__instance, service =&gt; service.LocalPlayer != null, "PlayerPatch");
        /// </code></example>
        public static RegisterResult TryRegisterAndLog<T>(T instance, Func<T, bool> isReady, string logSource) where T : class
        {
            var result = AutoRegisterIfReadyEx(instance, isReady);
            if (result == RegisterResult.Registered)
                global::ValheimJS.Utils.Logger.Shared.LogInfo(logSource, "Successfully registered " + typeof(T).Name + ".");
            else if (result == RegisterResult.AlreadyRegistered)
                global::ValheimJS.Utils.Logger.Shared.LogDebug(logSource, typeof(T).Name + " already registered.");
            return result;
        }

        /// <summary>Removes registrations whose Unity objects have been destroyed.</summary>
        /// <remarks>
        /// Call on Unity's main thread, for example after unloading a scene. Live Unity objects and managed
        /// services remain strongly held; this does not reevaluate readiness or remove managed services.
        /// Individual lookups also remove destroyed entries as they encounter them.
        /// </remarks>
        /// <example><code>
        /// // After scene teardown has destroyed its Unity objects:
        /// Container.Cleanup();
        /// // Managed session services still need explicit removal:
        /// Container.Clear&lt;SessionService&gt;();
        /// </code></example>
        public static void Cleanup()
        {
            lock (Sync)
            {
                var removed = new List<Type>();
                foreach (var pair in Entries)
                    if (!IsAlive(pair.Value)) removed.Add(pair.Key);
                foreach (var type in removed) Entries.Remove(type);
            }
        }

        // Caller holds Sync. Remove stale entries outside dictionary enumeration.
        private static bool TryGetAlive(Type type, out object instance)
        {
            if (Entries.TryGetValue(type, out instance))
            {
                if (IsAlive(instance)) return true;
                Entries.Remove(type);
            }
            instance = null;
            return false;
        }

        private static bool IsAlive(object instance)
        {
            if (ReferenceEquals(instance, null)) return false;
            var unityObject = instance as UnityEngine.Object;
            return ReferenceEquals(unityObject, null) || unityObject != null;
        }

        private static bool SameInstance(object left, object right)
        {
            var leftUnity = left as UnityEngine.Object;
            var rightUnity = right as UnityEngine.Object;
            if (!ReferenceEquals(leftUnity, null) && !ReferenceEquals(rightUnity, null))
                return leftUnity == rightUnity;
            return ReferenceEquals(left, right);
        }
    }
}
