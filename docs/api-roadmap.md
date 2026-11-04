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
| Storage | Plugin filesystem, document collections, small string storage; persistent account identity and publisher-owned shared metadata |
| Policy | Native roles, grants and denials; JS access queries; default admin/staff/player roles |
| Commands | Native registry, C#/JS command classes, aliases, leading options, async validation/execution, role/permission gates, static/contextual help |
| Administration | Private `!manage:roles`, `!manage:permission` and `!manage:user` commands; Valheim admin recovery and delegated management permission |
| Networking | Native channel/event scopes over RpcBus, notifications and synchronous request responders; explicit transient binary sync |
| World | Existing networked prefab spawning, session handles, ZDO data, object and prefab queries |
| Input | Client keyboard bindings and callbacks; no mouse API |
| Events | Coarse heartbeat, routed chat, peer/character changes and read-only damage observation |

These support rules, storage, private commands and coordination. They do not yet expose player
inventory, stats, teleports, item grants, custom assets, NPC rendering or general UI.
`ChatService` is retained but dormant; command replies use the separate active `CommandService`.

## Next gameplay APIs

| Candidate | Why it matters | Source/integration question |
|---|---|---|
| Player stats and movement | Rewards, moderation, world rules | Verify authoritative paths and object ownership before exposing health, stamina or teleport operations |
| Inventory snapshots and mutations | Shops, quests, crafting | Inventory is normally client-owned; no inventory mirror currently exists in ValheimJS |
| Item queries and grants | Rewards and shops | Wrap existing ObjectDB prefabs; distinguish item data from creating new assets |
| Build/interaction/crafting hooks | Regions, automation and quests | Choose funnels that run on the deciding process rather than assuming every client UI hook is authoritative |
| Opt-in damage cancellation | PvP rules and safe areas | Current damage hooks observe after the call; vetoing requires a different interception contract |
| General chat output | Announcements outside commands | Define presentation and sender/authority policy before reactivating or replacing dormant ChatService behavior |

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

- Client NPC renderer and dialog protocol, with authority-owned definitions and explicit readiness.
- Asset/content registration, deciding whether to integrate an established mod library or own the pipeline.
- Client UI beyond keyboard callbacks, with server-authorized actions sent through networking.
- Optional scheduling and localization helpers driven by demonstrated plugin needs.

A dedicated server cannot draw client UI. Existing networked prefabs can replicate through ZDOs;
new models and textures require an asset pipeline. Binary sync is an explicit codec/transport path,
not automatic replication, a client inventory mirror or an unreliable priority channel.

## Verification required for new APIs

Inspect the relevant installed mods and game source/decompilation, then verify the target in the
running client and dedicated server. Establish peer identity, authority and ownership before
allowing mutations. Add the native service first, then a thin binding, generated declarations,
focused probes and a user workflow. Keep planned APIs out of the current API reference.
