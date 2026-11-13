---
title: Runtime ownership and limits
description: How the current native services, scripting host and adapters fit together.
---

# Runtime ownership and limits

ValheimJS ships one BepInEx DLL. It classifies the executable once, initializes shared and selected
side modules, registers matching Harmony patches, then loads JS plugins. A player host uses the
client branch; session authority is checked live. See [Process branches](guides/process-branches.md).

## Native services own the behavior

| Responsibility | Owner | JS surface |
|---|---|---|
| Channel/event scopes, request completion and consumer cleanup | `Core/Networking.cs` | `net` binding and `Rpc` facade |
| Binary frame validation, codec, deltas and stream scopes | `Core/NetworkSync.cs` | `sync` binding and `Sync` facade |
| Routed RPC registration and wire-level request correlation | `Utils/RpcBus.cs`, `Networking/RoutedRpcTransport.cs` | Reached through native networking services |
| Identity/profile records and small publisher-owned account metadata | `Services/AccountService.cs` | `AccountService` queries and metadata operations |
| Verified connections mapped to persistent accounts | `Services/AccountSessions.cs`, `Core/PlayerIdentity.cs` | `AccountService.fromPeer(...)` |
| Default roles, role grants, direct grants/denials and permission catalogue | `Services/PermissionService.cs` | `PermissionService` queries; no JS editing methods |
| Command routes, aliases, access checks, async stages and invocation lifetime | `Core/ChatCommands.cs`, `Commands/` | `ChatCommand` and `ChatCommands` |
| Condition sampling, edges, per-handler isolation and cadence | `Utils/ConditionObserver.cs`, `Bindings/SignalBindings.cs` | `signals` binding and SDK `signals`/`predicates` |
| Player identity/state resolution (uid ↔ peer ↔ ZDO) | `Entities/PlayerDirectory.cs`, `Entities/Player.cs` | `players` binding and `ValheimPlayer` |
| World simulation registry, lifecycle, ZDO writes and ownership retention | `World/WorldManager.cs`, `World/WorldOwnership.cs` | `npc` binding; future world kinds |
| Relevance subscriptions and control messages | `World/WorldRelevancy.cs`, `World/WorldMessages.cs`, `World/WorldManager.cs` | Clients receive relevance diffs on `valheimjs:world` |
| NPC behavior, prefab recipe and client presentation | `World/Npc/NpcBehavior.cs`, `World/Npc/NpcPrefabs.cs`, `World/Npc/NpcPresenter.cs` | `npc` binding and `ValheimBaseNPC` |
| Declarative YAML panels and flat host UI | `Core/Ui/UiCompiler.cs`, `Bindings/UiBindings.cs`, `UI/`, `Core/ValheimJsHost.cs` | `ui` binding and SDK `ui` facade |
| Perception store, durable memory and YAML AI bridge | `Entities/NpcModels.cs`, `World/Npc/NpcBehavior.cs`, `Core/AiSenses.cs`, `Bindings/AiBindings.cs` | `npc.sensed`/`npc.memory`; `ai` with `ctx.state.senses` |
| Lazy navigation volume, pathfinder, cache and debug overlay | `World/Navigation/` | `nav` binding; `nav-debug` example |
| World-space line/mesh drawing and server replication | `Debugging/WorldDraw.cs`, `Bindings/DrawBindings.cs` | `draw` binding and SDK `draw` |
| Private command submission/replies over `valheimjs:commands` | `Networking/CommandService.cs`, `Hooks/CommandInputHook.cs` | `!` input and private `Commands` lines |
| Script engine, branch components, dependency order, contexts and timers | `Scripting/` | Plugin descriptors and host API |
| Native values, cadence, scoped registries and JS classes | `Scripting/Binding/` (`JsPayload`, `Ticker`, `ScopedBindings`, `JsCallback`), `Scripting/Types/` (`JsTypeRegistry`) | `ReturnKind.Native`, `[JsType]` classes, all bindings |
| Atomic per-document persistence | `Storage/DocumentDatabase.cs` | Plugin document store and host service stores |

Bindings adapt values and callbacks. The SDK shapes calls and types; it does not own subsystem
registries. Native services do not depend on JS binding classes. Game-specific hooks, transport
and connection adapters surround engine-independent cores tested by the headless harness.

`Networking/ChatService.cs` is deliberately dormant and is never constructed at startup. Its
retained code only receives server announcements on `valheimjs:chat/message` and displays a
`Server` shout-style chat line. It does not implement commands or the active command transport.

## Execution and failure boundaries

The main-thread update pumps native network completions every frame. JS timers/promise jobs pump
at `Runtime.TickMs` (200 ms by default). The `onServerTick` hook comes from a separate ~1 Hz
`ZNet.Update` patch and also fires on connected clients. Neither its name nor process side proves
authority; account/policy services require `ZNet.IsServer()` in a live session.

Setup/teardown, RPC responders and help callbacks are synchronous contracts. Commands await
their validation/execution tasks and recheck role/permission policy before both stages.
Ordinary event-hook promises are not awaited for results or command-style error handling.
Synchronous hook failures unload that plugin; a failing interval stops that interval; command
failure ends the invocation; request responder failure fails that request. Observed conditions
are sampled by the host and evaluate at most once per tick: a failing predicate keeps its last
value, and a failing handler is logged without stopping the other subscriptions or the plugin.

Callbacks and promise jobs restore their owning plugin context. Cross-plugin exports are ordinary
JS calls: capture provider-owned collections or scoped APIs when exporting operations. `use`
does not change the executing provider automatically or perform an RPC.

Shutdown unloads scripts while services remain usable, releases native command scopes, then stops
network scopes and RpcBus. Script reload reconstructs all branch components and clears their
in-memory registrations, timers and requests. Host account/policy services and durable files survive.

## Storage boundaries and known limits

| Store | Location | Intended use |
|---|---|---|
| Plugin documents/files | `data/<plugin>/` | Detailed private plugin state |
| Host string storage | `data/<plugin>/storage.json` | Small string key/value convenience store |
| Account store | `services/accounts/` | Verified identities, host profile fields, small shared publisher records |
| Policy store | `services/permissions/` | Role definitions, account policy and permission descriptions |

Account metadata is a JSON object capped at 16 KiB per publisher/account. Other plugins on the
authority may read it; only its publisher writes it. A shared bank-account id is a lookup link;
the bank's own exports still authorize and perform withdrawals/deposits.

The document backend caches collections. It is not a multi-process database, has no cross-document
transactions and does not automatically observe external edits. Writes fail explicitly and update
cache only after a successful file replacement. The string-storage convenience file uses its own
format and should not be treated as a JSON document database or transactional store.

The current document filename escaping can collide: `a/b` and `a_002fb` map to the same key. This
is a confirmed backend limitation, retained here because changing persisted key encoding requires
a separate data-format decision. Prefer already-safe stable ids such as account GUIDs and simple
collection names. Host account/publisher keys use GUIDs/hashes and do not need that escaping.

Isolation is host-enforced, not SDK-enforced. Scoped bindings receive the calling plugin's name
through a per-plugin closure, and their services owner-check every removal (`net.off`, `sync.off`,
command unregister/reply), so one plugin cannot release another's registrations. World spawn
handles are owner-tagged in `Core/SpawnHandleTable`: `destroy`/`alive`/`getData`/`setData` reject a
foreign handle, and `spawn`/`setData`/`destroy` require live authority plus ZDO ownership. The
remaining deliberate gap is naming: RPC channel/event and sync stream names are process-wide, so
two plugins can subscribe to or emit on the same name and `meta.sender` carries no plugin
attribution.

General player/inventory APIs, general chat broadcasting, new content registration and automatic
sync replication are not implemented. See the [roadmap](api-roadmap.md) for proposed work.

The heartbeat payload currently contains `world`, `peers`, `day` and `dayFraction`; use
`game().snapshot()` when you need `timeOfDay`. The SDK's `ServerTickPayload` currently aliases
the broader game-snapshot type, so that annotation alone does not guarantee the extra field.

## Verification coverage

`task check` covers typechecking, SDK unit tests, native compilation, generated-declaration drift,
bundling, binding/process/network/command/account/management/world-handle/world-manager/signals/
behaviour/ai probes and example reloads. `--probe-world-manager` covers relevance hysteresis/diffs, A* paths
(flat, blocked, stairs, water), nav-cache round-trips and draw/message DTOs. Role and help probes include actual JS registration through the native bindings,
not only facade mocks.

Development live checks exercised client/dedicated startup, private commands, Valheim admin
recognition, management CRUD, default-role persistence, account lookup/shared-data boundaries,
network requests/binary frames and contextual help. The player-host authority-transition check
was deferred; headless transition tests do not replace that live check. Live checks preceded the
final cleanup commit; the cleanup was verified with `task check` rather than another deployment.
