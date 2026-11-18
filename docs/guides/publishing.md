---
title: Publishing a plugin
---

# Publishing a plugin

A JS plugin is portable as a folder for compatible ValheimJS hosts. Package the entries and API
version you tested, and tell owners which processes need them; client/server availability and
live authority still determine which operations work.

## 1. Package it

```bash
cd scripting
bun run typecheck
bun run build.ts myplugin
```

Take `scripting/dist/myplugin/` with its manifest and built shared/server/client entries. Keep the
folder name aligned with the manifest name; changing plugin identity also changes its data/config
namespace. `build.ts` rejects a manifest `name` that differs from the source folder:

```
myplugin/
  manifest.ini
  shared.js       # optional
  shared.js.map   # optional; error locations fall back to the bundle without it
  server.js       # optional
  server.js.map
  client.js       # optional
  client.js.map
```

Zip that folder, or put it in a repository. Users drop it into `<server>/ValheimJS/plugins/`.
Keep the `.js.map` files next to their bundles; the host uses them to report the original
TypeScript file and line when a plugin throws.

## 2. Ship a config template

Create `config.ini` next to the bundle contents and tell users to copy it to
`ValheimJS/config/myplugin/config.ini`:

```ini
# myplugin settings — every value is a string
enabled=true
welcome=Welcome to the server!
announce_interval=300
```

Read values with `ctx.host.config("welcome")`, and always provide a fallback in code so a missing
file cannot crash the plugin:

```ts
const interval = Number(ctx.host.config("announce_interval") ?? "300");
```

## 3. Tell users what you need

In your README, list:

- the `apiVersion` you target (`1`)
- any `depends` entries and where to get them
- which entries belong on client, dedicated server or player hosts; whether peers need matching plugins
- command names/aliases, contextual help and required roles/permissions
- shared account metadata you publish and exports other plugins may call
- config keys and their defaults
- what the plugin writes under `ValheimJS/data/<name>/`
- whether it is safe to enable on an existing world

## 4. Versioning

- **patch** — bug fix, no behaviour change users must handle
- **minor** — new command, new config key, additive exports
- **major** — breaking: changed command names, removed config keys, changed exports

Bump both `version` in `manifest.ini` and in `definePlugin`, and keep them equal. Plugins that
depend on you use the manifest value.

## 5. Safety notes worth repeating

- Handlers run on the owning process's main thread: document expensive work and gate authority explicitly.
- RPC notifications have no delivery confirmation; do not build flows that need acknowledgement.
- Never store credentials in `ValheimJS/data/` — it is plain text next to the world files.
- Key durable per-user records by the AccountService account id; names and session `uid` are not durable keys.
- If you need an API that does not exist yet (player state, item grants), say so
  rather than shipping a plugin that pretends to have it.
