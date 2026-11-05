---
title: Engine contract
description: The native layer every binding and game facade type must use.
---

# Engine contract

This is the standing contract for scripting work. New bindings, facades and refactors follow it;
existing ones are migrated when touched. Backward compatibility is not a constraint for internal
shapes — only the public JS surface documented in `guides/api-reference.md` is.

## Layers

| Layer | File | Rule |
|---|---|---|
| Native values | `src/Scripting/Binding/JsPayload.cs` | Return structured data as `JsValue` with `ReturnKind.Native`; never build JSON strings for API responses. `JsPayload` builds plain JS objects/arrays; no CLR type reaches script. |
| Cadence | `src/Scripting/Binding/Ticker.cs` | Bindings never own a `Stopwatch`. Use `Ticker.Due()` (interval) or `Ticker.Sample()` (elapsed ms). |
| Scoped registries | `src/Scripting/Binding/ScopedBindings.cs` | Bindings with per-plugin registrations derive from `ScopedBindings<TKey,TValue>`; override `OnRelease` for cleanup. No per-binding `Dictionary<string, Dictionary<…>>` + release walk. |
| Callbacks/context | `src/Scripting/Binding/JsCallback.cs`, `JsContextScope.cs` | One save/restore for `__vjs_context`. Use `JsCallback.Invoke`/`InvokeOn`; no private context helpers, no catching-and-reformatting per binding. |
| Native classes | `src/Scripting/Types/` | Game-facing classes are registered once (`RegisterType<T>()` / `Define<T>(name)`), producing a real `Constructor` + prototype + per-plugin instance cache. |
| Declarations | `src/Scripting/Binding/JsBindingDeclarations.cs` | Signatures come from metadata. `Returns` is mandatory for `Native` so the TS type cannot drift. |

## Game facade types

- C# class represents the game thing (`ValheimJS.BaseNPC`, `ValheimJS.Player`, `ValheimJS.Container`);
  the JS name is explicit (`[JsType("ValheimBaseNPC")]` or `Define<T>("ValheimNPC")`).
- Expose only user-facing, long-lived, owner-checked members. Everything else is `[JsIgnore]`.
- Reflection happens once at registration; members compile to `ClrFunction`/descriptors.
- The calling plugin is resolved per call from `__vjs_context`; never capture it in a class.
- One native handle per game entity, one JS instance per plugin, identity stable for that plugin.
- Constructors take an opaque native handle and validate existence/ownership. Plugin subclasses
  (`class Settler extends ValheimBaseNPC`) call `super(handle)`; the host constructs them with
  `Engine.Construct(subclass, [handle])`.
- Classes are engine-global; plugin unload drops that plugin's instance cache. Do not rely on GC
  for native resources.

## Binding checklist

1. Structured return? `ReturnKind.Native` + `JsPayload` + `Returns`.
2. Per-plugin registrations? `ScopedBindings<…>`.
3. Cadence? `Ticker`.
4. Callbacks? `JsCallback` (and `InvokeOn` when `this` matters).
5. Engine-global game type? register it in `Install(JsTypeRegistry)` and expose instances through
   `JsTypeRegistry.Instance`; projections must be owner-checked.
6. Tests: add or extend the matching harness probe; declarations must regenerate cleanly.

## Status

Implemented: `JsPayload`, `Ticker`, `JsContextScope`, `ScopedBindings`, `JsCallback.InvokeOn`,
`JsTypeRegistry` + `[JsType]`/`[JsName]`/`[JsIgnore]`, `ReturnKind.Native`, engine/registry wiring
into `JsBindingContext`/`JsRuntime`/`JsBindings.Install`. Migrated: `game`, `world` proximity
queries, `signals`, and the first game facade: `players` + `ValheimPlayer` and `npc` + `ValheimBaseNPC`
(class declarations generated from `[JsType]` metadata, with `[JsNullable]` for nullable members).

Next: NPC dormancy/combat/interaction surfaces, then containers. See `docs/api-roadmap.md`.
