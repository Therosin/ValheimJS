---
title: API roadmap
description: Current capabilities and researched future work, kept separate.
---

# API roadmap

This is a planning document. The [API reference](guides/api-reference.md), generated SDK declarations
and [runtime ownership map](runtime.md) describe what exists now. Names below marked proposed
are design sketches, not functions a plugin can call.

## Available now

| Area | Current support |
|---|---|
| Startup | Optional shared/server/client entries, one context per plugin, fixed process side and separate live authority |
| Storage | Plugin filesystem, read-only shipped assets, document collections, small string storage; persistent account identity and publisher-owned shared metadata |
| Policy | Native roles, grants and denials; JS access queries; default admin/staff/player roles |
| Commands | Native registry, C#/JS command classes, aliases, leading options, async validation/execution, role/permission gates, static/contextual help |
| Administration | Private `!manage:roles`, `!manage:permission` and `!manage:user` commands; Valheim admin recovery and delegated management permission |
| Networking | Native channel/event scopes over RpcBus, notifications and synchronous request responders; explicit transient binary sync |
| World | Existing networked prefab spawning, session handles, ZDO data, object and prefab queries |
| NPC / AI | Server-simulated NPCs with perception store, memory, inventories/stats, native plans, planner/utility hosts and YAML behaviour goals |
| UI | Client-only declarative YAML panels; humanoid NPCs open the vanilla `InventoryGui` container grid |
| Input | Client keyboard bindings and callbacks; no mouse API |
| Events | Coarse heartbeat, routed chat, peer/character changes and read-only damage observation |

These support rules, storage, private commands, coordination, NPC bodies/inventories and client
panels. They do not yet expose player-owned inventory/stats, teleports, item grants or custom UI
assets. NPC inventory access and the vanilla NPC container UI are separate from player inventory.
`ChatService` is retained but dormant; command replies use the separate active `CommandService`.

## Binding gaps for NPCs and containers

The world surface is deliberately read-mostly. These are the known missing pieces for the AI and
container work, kept here so implementations do not route around them:

Added:
- [x] `world.idOf(handle)` — stable ZDO id for a spawn handle (durable reference)
- [x] `world.charactersNear(x,y,z,radius,limit)` — read-only character scan for discovery/AI groundwork
- [x] `world.containersNear(x,y,z,radius,limit)` — container presence, slots and item count
- [x] `game.peerList()` — routed ids, names, player ids, server/ready flags

Outstanding:
- [ ] `world.object(id)` — resolve a ZDO id to position, prefab and owner (the inverse of `idOf`)
- [ ] authority-gated `world.setData(id, key, value)` / `world.removeData(id, key)` for non-spawned ZDOs
- [x] container contents through `npc.container(id).items` and the NPC inventory accessor
- [ ] container mutation: ownership claim, add/remove/move, slot limits for arbitrary world containers
- [ ] character stats/mutations: owned health/stamina/eitr writes through the owner-op primitive
- [ ] inventory handles: player inventory query/grant/remove behind the same async contract
- [x] interactable discovery through the NPC perception store (`view.objects`)
- [x] NPC cooking/fire adapters with conversion inputs and fuel state
- [ ] character ZDO ownership and authority helpers for plugin-driven entities
- [x] NPC base: server-authoritative `WorldManager`/`NpcBehavior`, prefab registration, perception,
      intents, durable state, rehydrate, relevance subscriptions
- [x] Perception store with characters and world objects, relative/directional queries and
      short-term recognition memory
- [x] Per-NPC durable memory (`npc.memory`) and YAML behaviour-tree binding (`ai`) with automatic
      `ctx.state.senses` injection
- [x] World-space `draw` API for lines/meshes; server calls replicate to clients
- [x] NPC combat/interaction surfaces and native action settlement
- [ ] NPC dormancy records
- [x] hearing/noise, equipment and per-NPC damage events as perception sources
- [ ] entity state: declared fields mapped to ZDO keys vs coalesced sync, with join snapshots

## Next gameplay APIs

| Candidate | Why it matters | Source/integration question |
|---|---|---|
| Player stats and movement | Rewards, moderation, world rules | Verify authoritative paths and object ownership before exposing health, stamina or teleport operations |
| Player inventory snapshots and mutations | Shops, quests, crafting | NPC inventory and the vanilla NPC container UI exist; player inventory remains client-owned and needs an authority/mirror decision |
| Item queries and grants | Rewards and shops | Wrap existing ObjectDB prefabs; distinguish item data from creating new assets |
| Build/interaction/crafting hooks | Regions, automation and quests | Choose funnels that run on the deciding process rather than assuming every client UI hook is authoritative |
| Opt-in damage cancellation | PvP rules and safe areas | Current damage hooks observe after the call; vetoing requires a different interception contract |
| General chat output | Announcements outside commands | Define presentation and sender/authority policy before reactivating or replacing dormant ChatService behavior |

See [NPCs and AI](guides/npc-ai.md) for the current end-to-end example.

The installed mod research suggests where to inspect, not what ValheimJS already implements:
EpicLoot for item/loot and crafting paths; KG Marketplace for client-rendered NPCs, dialogs and
quests; Innangard for building; DvergerAutomation for containers. Local decompiled references live
under the workspace's ignored mod-research folders and are not shipped with this repository.

## Build on accounts without growing the shared record

Persistent identity is already implemented. Plugins should store detailed state in their own
collections keyed by the ValheimJS account id. Publish only small links when another plugin needs
to discover an owned record. For example, a bank publishes its own bank-account id; consumers use
`use("bank", bank => ...)` to call the bank's exported account operations. See [Accounts](guides/accounts.md).

A future user-management plugin API would need a separate capability model. The current JS
surface intentionally does not edit private profiles, role definitions or policy. Native private
management commands provide administrative editing today.

## Content and presentation work

- NPC dialogs and richer presentation; the renderer, relevance subscription and server-owned simulation exist.
- Asset/content registration, deciding whether to integrate an established mod library or own the pipeline.
- Additional client UI beyond declarative panels and the native NPC container UI, with
  server-authorized actions sent through networking.
- Optional scheduling and localization helpers driven by demonstrated plugin needs.

A dedicated server cannot draw client UI. Existing networked prefabs can replicate through ZDOs;
new models and textures require an asset pipeline. Binary sync is an explicit codec/transport path,
not automatic replication, an automatic player-inventory mirror or an unreliable priority channel.
The NPC inventory UI uses an explicit server-authoritative snapshot, not generic sync replication.

## Verification required for new APIs

Inspect the relevant installed mods and game source/decompilation, then verify the target in the
running client and dedicated server. Establish peer identity, authority and ownership before
allowing mutations. Add the native service first, then a thin binding, generated declarations,
focused probes and a user workflow. Keep planned APIs out of the current API reference.
