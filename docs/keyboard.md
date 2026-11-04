---
title: Keyboard bindings
description: Register keyboard actions from JavaScript and C#.
---

# Keyboard bindings

`input` maps a named action to one key and optional press, hold and release callbacks. It is
client-only and installed by the `input` binding class; all state is host-owned.

```ts
import { definePlugin, input, log } from "../../../../sdk/index";

export default definePlugin({
    version: "1.0.0",

    setup() {
        input.bind("togglePanel", "F8");
        input.on("togglePanel", "press", () => {
            log("toggle");
            return true;          // claim the key for this frame
        });
    },

    teardown() {
        input.clear();            // removes only this plugin's entries
    }
});
```

Each action has at most one callback per phase (release it with `input.off`). Hold callbacks run
each frame the key is pressed. Registering a binding again changes its key. `input.state(action)`
binds a raw key name on first lookup; `input.rawState(key)` never registers anything. State is
`null` when the key or keyboard is unavailable.

Returning `true` sets `input.handledAny()` for that frame. Valheim's named `ZInput` button reads are
masked when their resolved keyboard control matches a claimed key; unrelated controls and active
alternative button controls are left alone. This does not suppress direct low-level key reads or
every UI path, and does not ignore text fields — handlers must account for their own context.

Bindings and callbacks are scoped per JS plugin and removed on reload or unload. The C# side can
use `ValheimJS.Input.KeyboardManager` directly; `Clear()` removes all native and script entries, so
use it only for global shutdown. Keyboard support requires the `UseInput` build group and is not
installed on a dedicated server. There is no mouse API yet.
