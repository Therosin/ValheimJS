---
title: Process branches
description: Choose which code starts on each Valheim process and share state between entries.
---

# Process branches

Put code that should always run in `shared/main.ts`. Put dedicated-server work in `server/main.ts`
and local UI/input work in `client/main.ts`. The host selects entries before evaluating them,
so the opposite branch cannot initialize accidentally.

```text
scripting/plugins/session/         scripting/dist/session/
  manifest.ini                      manifest.ini
  shared/main.ts                     shared.js
  server/main.ts                     server.js
  client/main.ts                     client.js
```

Each entry is optional. A plugin needs at least one; a plugin with only the opposite branch is
skipped on this process. Set `apiVersion=1` in the manifest. There is no manifest `entry` field.

## Pick the branch before loading code

ValheimJS selects the side once in BepInEx `Awake()`, using the executable name: `valheim_server`
is server and `valheim` is client. Unknown executable names stop initialization with a message
asking for an override. For renamed launchers, set this in the BepInEx settings file:

```ini
[General]
ProcessSide = server
```

The default is `auto`; the other explicit choice is `client`. Restart the process to change it.
Batch mode and graphics availability do not select a branch.

A player hosting a world still uses the **client branch**. Process side answers which program
started; world authority answers whether that program currently runs the world. Before a session
exists, `game().isServer()` is false. `game().sessionReady()` reports whether `ZNet` exists, not
whether connection, world loading, or player spawning has completed.

## Shared and side code form one plugin

Every entry exports its own `definePlugin(...)` object or class. The host evaluates shared and
runs its synchronous `setup`/`onLoad`, then evaluates and initializes the selected side. Plugin
context is available during entry evaluation; class `this.ctx` is attached after construction,
before setup. Do not return a promise from setup or teardown.

```ts
// shared/main.ts
import { definePlugin } from "../../../sdk/index";

export default definePlugin({
    exports: { greeting: "hello" },
    setup(ctx) { ctx.state.started = true; }
});
```

```ts
// client/main.ts
import { definePlugin, log } from "../../../sdk/index";

export default definePlugin({
    setup(ctx) {
        log(`${ctx.exports.greeting}; shared ready: ${ctx.state.started}`);
    }
});
```

Both components share the same `ctx`, `ctx.state`, `ctx.exports`, configuration, API, storage,
and dependencies. Descriptor-declared exports are merged into `ctx.exports`; duplicate names
fail the plugin. Dependencies see the combined exports after all selected entries initialize.

Matching event handlers run shared first, then side. Class handlers retain their instance as
`this`; object handlers receive `(ctx, payload)`. At most one component may return a non-null,
non-undefined synchronous result for an event. Conflicting results disable the plugin.

Unload runs side teardown before shared teardown and attempts both even if one fails. A failed
load rolls back plugin-owned registrations and timers. A failing hook disables and unloads the
whole plugin; unrelated plugins continue. Reload creates fresh components and shared state.

Import reusable helpers from ordinary source files. Do not import another entry's `main.ts`:
that bypasses selection and can execute entry code twice. Imports bundled into separate entries
are separate module instances; use `ctx.state` or `ctx.exports` for shared runtime state.

## Gate individual calls in shared code

Immediate gates work during bundle evaluation, setup, and host callbacks:

```ts
import { serverOnly, clientOnly, authorityOnly } from "../../../sdk/index";

serverOnly(ctx => { ctx.state.role = "dedicated"; });
clientOnly(ctx => { ctx.state.role = "client"; });
authorityOnly(ctx => { ctx.state.hasAuthority = true; });
```

These execute once when called and return the callback result, or `undefined` when skipped.
They never schedule work for a future session. Recheck authority in a world event or timer when
you need it after startup. A player host passes `clientOnly` and can also pass `authorityOnly`.

Use `serverHandler`, `clientHandler`, or `authorityHandler` for object-form event handlers:

```ts
import { definePlugin, authorityHandler } from "../../../sdk/index";

export default definePlugin({
    onPlayerJoin: authorityHandler((ctx, player) => {
        ctx.state.lastJoined = player.name;
    })
});
```

See the bundled `branches` example for shared and both side entries working together.

## C# uses the same process rules

ValheimJS ships one DLL. Startup modules and Harmony patch classes require exactly one of
`[Shared]`, `[ServerOnly]`, or `[ClientOnly]` from `ValheimJS.Scripting`. The loader validates
all annotations before constructing modules or applying patches, then initializes shared and
matching-side modules. An attribute does not intercept arbitrary method calls.

Bindings use `[JsBinder(..., Availability = JsAvailability.Client)]` or `Server`; inactive
binders are filtered before construction. `Dedicated` has the same server-process meaning.
Use `GameEnvironment.ServerOnly(Action)`, `ClientOnly(Action)`, and `AuthorityOnly(Action)` for
individual C# calls in shared code. Authority is a live check, independent of process attributes.
