# ValheimJS — agent notes

## Automation

Before running ad-hoc commands, check the project tasks (`task --list`, `task --list-all`) and
prefer an existing task when it covers the work. Invoke tasks as `task -y <task> ...` so prompts do
not block; inspect unfamiliar or consequential tasks (deploy, start/stop, restart) before running.

| Task | Does |
|---|---|
| `task build` | compile the plugin (`bin/Release/netstandard2.1`) |
| `task bundle` | bundle TypeScript plugins into `scripting/dist` |
| `task check` | typecheck + JS tests + build + bundle |
| `task deploy:client` / `task deploy:server` | build, bundle, then copy plugin + JS plugins |
| `task start` / `stop` / `restart` | dedicated server lifecycle |
| `task logs:server` / `logs:client` | follow a BepInEx log |

Target paths come from `.env` (gitignored). `scripts/deploy.ps1` reads them; do not hardcode paths.

## Layout

`src/Scripting` engine · `src/Scripting/Binding` binding infrastructure · `src/Bindings` game API modules
(`[JsBinding]`) · `src/Networking` mod-owned RPC · `src/Hooks` capture points · `src/Core` registry,
bridge, player access · `src/Utils` helpers · `scripting/` TS workspace · `docs/guides` modder docs ·
`docs/api-roadmap.md` and `docs/networking.md` dev docs.

## Rules

- Plugin code is untrusted: bound engine work, keep hooks cheap, never do per-tick broadcasts.
- Prefer real ZDOs for world objects and ZDO custom data for object state over custom sync.
- Verify in the running game; the headless harness (`harness/`) only covers the host.
