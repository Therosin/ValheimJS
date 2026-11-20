---
title: Getting started
---

# Getting started

Create a separate Bun project for your plugin. You need ValheimJS installed under BepInEx on
the process that will run it. The SDK is a build-time dependency; the game runs the bundled JS
with Jint, not Bun. Use a ValheimJS host that supports ES module plugin entries.

## Create a project

```bash
mkdir greeter
cd greeter
bun init -y
bun add github:Therosin/ValheimJS#main
mkdir -p src dist/greeter
```

Pin a commit or release instead of `main` when you want reproducible builds.

Create `src/shared.ts`:

```ts
import { definePlugin, log } from "@valheimjs/sdk";

export default definePlugin({
    setup(ctx) {
        log(`greeter started; data: ${ctx.paths.data}`);
    },
    onServerTick(ctx, snapshot) {
        if (ctx.state.announced) return;
        ctx.state.announced = true;
        log(`world: ${snapshot.world ?? "(loading)"}`);
    }
});
```

Create `dist/greeter/manifest.ini`:

```ini
name=greeter
version=1.0.0
apiVersion=1
enabled=true
```

## Build and install

```bash
bun build ./src/shared.ts --target=browser --format=esm --outfile=./dist/greeter/shared.js --sourcemap=linked
```

The output must be a self-contained ES module whose default export is the descriptor from
`definePlugin`. Bundle imported dependencies; Jint does not provide Bun, Node's filesystem API,
the browser DOM, or npm package resolution. Other bundlers can be used if they produce the same
output contract.

Copy the `dist/greeter` folder to `<game root>/ValheimJS/plugins/greeter/`, then restart the game
process. Check the BepInEx log for the host's loaded plugin count and
`ValheimJS/logs/greeter.log` for your message. You can enable `Dev/WatchForChanges` in the host
configuration to reload while developing; reload clears in-memory state and registrations.

To add process-specific code, bundle separate entries to `server.js` and `client.js`. The host
loads `shared.js` first and then the matching side entry; a player host uses the client branch.
See [Process branches](process-branches.md) and [Plugin anatomy](plugin-anatomy.md).

The SDK is built from TypeScript source. The GitHub dependency requires a toolchain that can
consume TypeScript from dependencies; Bun supports the workflow above.
