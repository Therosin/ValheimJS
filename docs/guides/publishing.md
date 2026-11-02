# Publishing a plugin

A plugin is portable as a folder. Nothing is compiled per server, so what you tested is what other
owners run.

## 1. Package it

```bash
cd scripting
bun run typecheck
bun run build.ts myplugin
```

Take `scripting/dist/myplugin/` — it contains `plugin.js` and `manifest.ini` — and rename it to
the plugin name if you like:

```
myplugin/
  manifest.ini
  plugin.js
```

Zip that folder, or put it in a repository. Users drop it into `<server>/ValheimJS/plugins/`.

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

- the `apiVersion` you target (`1` today)
- any `depends` entries and where to get them
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

- Handlers run on the server's main thread: document anything expensive, and default to doing less.
- `chat.send` is best effort; do not build flows that need acknowledgement.
- Never store credentials in `ValheimJS/data/` — it is plain text next to the world files.
- Do not assume a player's name is unique or persistent; use the session `uid` for live state.
- If you need an API that does not exist yet (world edits, combat events, custom RPC), say so
  rather than shipping a plugin that pretends to have it.
