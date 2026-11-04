# ValheimJS — agent notes

## Automation

Before running ad-hoc commands, check the project tasks (`task --list`, `task --list-all`) and
prefer an existing task when it covers the work. Invoke tasks as `task -y <task> ...` so prompts do
not block; inspect unfamiliar or consequential tasks (deploy, start/stop, restart) before running.

| Task | Does |
|---|---|
| `task build` | compile the plugin (`bin/Release/netstandard2.1`) |
| `task bundle` | bundle TypeScript plugins into `scripting/dist` |
| `task declarations` | regenerate `scripting/sdk/api/generated.ts` from the bindings |
| `task declarations:check` | fail when checked-in declarations drifted |
| `task probe` | harness probes: discovery, validation, Jint invocation, scoping, cleanup |
| `task harness` | load the bundled example plugins in the headless host |
| `task check` | typecheck, tests, build, drift, bundle, probe, harness |
| `task deploy:client` / `task deploy:server` | build, bundle, then copy plugin + JS plugins |
| `task start` / `stop` / `restart` | dedicated server lifecycle |
| `task logs:server` / `logs:client` | follow a BepInEx log |

Target paths come from `.env` (gitignored). `scripts/deploy.ps1` and `scripts/declarations.ps1`
read them; do not hardcode paths.

## Layout

Live targets are recorded in the parent `.unity.env` and local `.env`:
`CLIENT_GAME_MCP_PORT=8080`, `SERVER_GAME_MCP_PORT=8082`. One MCP connection registers `client`
and `server`; bridge calls use explicit `target`. Check game paths before mutation. Ask the
user to close Valheim before deploying client files and confirm the process exited. The
dedicated server lifecycle is agent-managed. The test server password is supplied by the user;
use the configured Task value rather than inventing one.

`src/Scripting` engine · `src/Scripting/Binding` attributes, discovery, Jint bridge, declaration
writer · `src/Bindings` game API classes (`[JsBinder]`) grouped as `game`, `world`, `net`, `input`,
`fs`, `db` · `src/Core` bridge seam, identity helpers, registries · `src/Hooks` capture points ·
`src/Utils` RPC bus, JSON helpers · `scripting/sdk` generated types + thin facades · `scripting/lib`
optional imported libraries · `docs/guides` modder docs · `harness/` host without Unity.

## Rules

Networking ownership: `src/Core/Networking.cs` owns the channel/event service and consumer
scopes over `Utils.RpcBus`; `src/Core/NetworkSync.cs` owns binary sync and delta/codec logic.
`src/Networking` contains networked services (such as `CommandService`) and the transport adapter.
`ChatService` is deliberately dormant; keep its code without constructing it at startup.
The native command registry belongs in `Core/ChatCommands`; JS `ChatCommands` is an adapter.
`Services/AccountService` owns identity/profile records and small publisher-owned shared metadata;
`Services/PermissionService` separately owns roles, grants and denials. Both reuse DocumentDatabase
under `services/`, outside plugin data sandboxes. Plugins may query accounts/policy and read other
publishers' metadata, but only write their own. Do not expose profile or policy management to JS.
Bindings only adapt JS values/callbacks and delegate; never put networking registries or peer
discovery in bindings, and never let native services depend on binding classes.

- Plugin code is untrusted: bound engine work, keep hooks cheap, never do per-tick broadcasts.
- The C# side is authoritative. Add a `[JsBinder]` class, then run `task declarations`; never
  hand-edit `generated.ts`.
- The SDK must not own subsystem state, dispatch, timers or registries. Facades only shape calls
  over host-provided API objects and host callbacks.
- Prefer real ZDOs for world objects and ZDO custom data for object state over custom sync.
- Verify in the running game; the headless harness (`harness/`) only covers the host.
