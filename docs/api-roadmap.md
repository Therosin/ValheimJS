---
title: API roadmap — what real plugins need
---

# API roadmap — what real plugins need

Review of the gap between the current scripting surface and mods like the ones installed
(Marketplace/NPCs/Quests, EpicLoot, Jotunn, Innangard, DvergerAutomation). Dev-facing: this is
the "what to build next and why" document, not modder docs.

## Today

| Area | Available |
|---|---|
| Data | `fs` (sandboxed), `db` (per-plugin documents), `ctx.host.storage`, `ctx.paths` |
| Game | `game.side/dedicated/isServer/isClient/singlePlayer/peers/instance/worldName/day/dayFraction/timeOfDay/snapshot` |
| World | `world.zdoCount/zdoAvailable/spawn/destroy/prefabs/exists/getData/setData/alive` |
| Messaging | `Rpc` channels (notifications + synchronous requests) over the host `net` binding |
| Input | `input.bind/on/off/state/...` (client only) |
| Events | `setup`, `teardown`, `onServerTick` (~1 Hz), `onChat`, `onPlayerJoin/Leave`, `onPlayerSpawned/Despawned`, `onDamage` |

Useful for: spawnable objects and object data, rules and policy, metrics, cross-process events,
client-side input, persistence. **Not** useful yet for: changing players, inventories, items, chat
output, host commands, NPC behaviour, UI.

## How the installed mods actually do it

| Capability | Mod / file | Mechanism | Consequence for us |
|---|---|---|---|
| NPCs + vendor dialogs | KG `Marketplace.Modules.SpawnedNPCs/SpawnedNPCs_Client.cs` | NPCs are **client-side** `Object.Instantiate` calls; the server only holds positions/state and syncs it | Server-only JS cannot make a visible NPC. Needs a client bundle (we have sides + networking) **plus a client render API** |
| Quests | KG `Quests_Main_Server.cs` (564 lines) + `Quest_ProgressionHook.cs` (633) | Server-side state machines driven by game events, synced to a client UI | Once events + persistence + messaging exist, quests are ordinary JS logic — no new engine feature |
| Shops / traders | KG `ItemManager/Trade.cs`, `Trader.cs` | Inventory + per-player currency in custom data | Needs inventory API and per-player persistence |
| Enchantments / custom items | EpicLoot `Common/ItemBatchLoader.cs`, `AssetBundleLoader.cs` | Clone item prefabs, mutate `ItemData`/custom data; new art ships as embedded **AssetBundles** | Stats/behaviour: needs inventory+item API. New models: needs a Unity asset pipeline, not JS |
| Territory rules (PvP, no-build, heal zones…) | KG `API/Marketplace_API.cs` (`TerritoryFlags`) | Area lookup + patches on damage/build/move | Needs area helpers, damage/build hooks, per-area config |
| Container automation | DvergerAutomation | Patches on container/inventory interactions | Needs container/inventory events |
| Building tweaks | Innangard | Patches on placement | Needs placement hooks |

Rule of thumb from the code: **the server owns state, the client owns pixels.** Every "NPC/quest"
mod is exactly that split.

## Gap matrix — P0 (unlocks most real plugins)

Everything below is a small binding module; game APIs verified in
`assembly_valheim` unless marked *verify*.

| Need | Proposed binding | Backing API |
|---|---|---|
| Move a player | `players.teleport(uid, x, y, z)` | `Character`/`Player` position set *verify* |
| Health / stamina / eitr | `players.setHealth(uid, v)`, `players.stats(uid)` | `Character.SetHealth/GetHealth`, ZDO fields |
| God mode / invulnerable | `players.setGodMode(uid, on)` | `Player.SetGodMode(bool)` |
| Skills | `players.raiseSkill(uid, name, factor)` | `Skills.RaiseSkill(SkillType, float)` |
| Inventory read/write | `players.inventory(uid)`, `players.giveItem(...)`, `players.takeItem(...)` | `Inventory.*`, `ObjectDB.GetItemPrefab` |
| Chat output | `chat.send/sendTo` | routed `ChatMessage` / `ChatAgent` |
| Chat commands | host-registered handlers (`commands.register`) | `Chat` capture + host dispatch |
| Events: interaction | `onInteract` | `Interactable.Interact` |
| Events: container | `onContainerChanged` | `Container`/`Inventory` patch |
| Events: craft | `onCraft` | `InventoryGui.DoCrafting` / `CraftingStation` *verify server path* |

## P1 — framework features that make plugins product-quality

| Feature | Why | Shape |
|---|---|---|
| **Inventory mirror** | inventory is owned by the client, so the server answers from a snapshot the owner publishes (`valheimjs:player-state`); writes stay commands and republish | implemented |
| **Per-player persistent store** | quests, currency, cooldowns, "seen this NPC" | `players.profile(uid)` → get/set JSON in `data/<plugin>/players/<uid>.json`, loaded on join, flushed on leave; survives restarts, keyed by name+sid as identity is session-only |
| **Scheduler** | daily events, resets, timed quests | `schedule.atDay(day, fn)`, `schedule.everyMinutes(n, fn)` (JS-level on `onServerTick`, no binding) |
| **Area rules** | territory flags, safe zones, PvP regions | `areas.define(name, {x,z,radius,rules})`, `areas.at(x,z)` + gating helpers around damage/build/interact |
| **NPC wrapping** (see below) | server-controlled NPCs with behaviour + dialog | `npcs.*` binding over real ZDOs + `Rpc`; client bundles only for presentation |
| **Dialog protocol** | NPCs that "talk" and offer actions | shared JS module: server sends `npc:open` with options, client renders via NPC text/HUD, replies `npc:choose` |
| **Item grants & loot hooks** | rewards, shops, drops | P0 inventory + `onDeath`/`CharacterDrop.GenerateDropList` postfix |
| **Localization** | plugin text on both sides | `text.key(k)` reading `config/<plugin>/localization.json` (JS-only) |


## Transport strategy — the right packet for the job

The framework has three channels. Using the wrong one is what makes scripting servers feel laggy.

| What you are moving | Use | Why |
|---|---|---|
| Existence, transform, animation, health of a **world object** | a real ZDO — `spawn.create(...)` | the game already replicates ZDOs with interest management, dirty-bit deltas and save integration. Free of our code, and clients cull what they cannot see |
| **Data attached to an object** (NPC identity, stock, state) | `spawn.set/get(handle, key, value)` | lives on the ZDO: replicated on the object's own dirty bits and saved with the world. No custom packets, no polling |
| **Ephemeral events** between processes (dialog choice, HUD hint, "reload config") | `Rpc.channel(channel).emit(event, data)` | one small routed call per event. No state, no cadence |
| **Per-player progress** that must survive a restart (quests, currency) | `fs` / per-player profile files | zero network cost; read on join, write on change |
| Continuous motion of something that is **not** a ZDO | don't — make it a ZDO, or throttle to ≤5 Hz and send deltas | per-tick broadcasts are the classic way to tank a server |

Rules that follow from this:

1. **Never broadcast on the tick.** `onServerTick` is for decisions; if state changed, send once.
2. **Prefer ZDO data over RPC** for anything the client needs to keep, because the game already
   handles persistence and late joiners for you.
3. **Keep payloads small** (well under 1 KB). If you are shipping a list, ship an id and let the
   other side read the rest from ZDO data or files.
4. **Do not sync what can be derived.** Client-side `env.peers()`/`players.list()` already exist.
5. **Late joiners are the test.** If a client joins after your event, it must still converge —
   that is automatic for ZDO data, and your responsibility for `Rpc` messages (send a
   snapshot on `onPlayerJoin`).

### Choosing for NPCs

```
NPC creature (vendor, quest giver, guard)
  existence + transform + animation → ZDO (spawn.create)
  identity + stock + quest tags      → spawn.set on that ZDO
  dialog choices / one-off effects   → Rpc, on demand
  per-player quest progress          → files (profile store), not the network
```

No custom sync loop is involved anywhere, which is the point: the server decides, the game's ZDO
layer does the heavy lifting, and our channels carry only what is genuinely new information.

## NPCs: wrap, don't fake

Marketplace-style NPCs are client-side `Instantiate` calls with server-held positions — the server
cannot see them, they have no physics, and nothing can attack them. That works for vendors only
because the mod ships its own UI and interaction.

The better primitive for us is the Inverse of that: **wrap a real, networked entity with
server-side behaviour**. Think CryEngine's `Net.Expose` — the entity is already replicated by the
game; we attach authority-side state and logic, and let clients see it through the engine's own
sync plus small presentation messages.

### Two kinds of NPC, one API

| Kind | How it exists | When to use |
|---|---|---|
| `creature` | `world.spawn(prefab, pos)` → real ZDO; the game already networks, animates and simulates it | vendors, quest givers, guards, animals — anything that should be visible, collide, and persist in the save |
| `visual` | server state + client-bundle instantiation (Marketplace style) | pure decoration or UI actors; no physics, no save entry |

Prefer `creature`. It gets persistence, save/load, ownership transfer and visibility for free;
`npcs.wrap` then only adds behaviour and dialog.

### Lifecycle

```
npcs.define({ prefab, x, z, name, tag, behaviour })
  server: spawn (real ZDO if the prefab is a creature) + register in data/<plugin>/npcs.json
          broadcast "npc:upsert" {id, name, tag, x, y, z, meta}
  client: on "npc:upsert" — attach hover/talk text to the ZDO when it appears;
          for `visual` kind, instantiate the prefab locally
npcs.remove(id)      → destroy ZDO (creature) or broadcast "npc:remove" (visual)
npcs.setState(id, …) → stored server-side; broadcast "npc:state" on change
npcs.onInteract(id, handler)  → server decides the dialog, replies over Rpc
```

State that must survive restarts lives in the ZDO (`zdo.set(id,"key",value)`) or in
`data/<plugin>/npcs.json` for the registry. Never in `ctx.state`.

### API sketch

```ts
// server (automatically no-op on a client process)
const ragnar = npcs.define({
    prefab: "Humanoid_Farmer",       // existing prefab only
    x: 120, z: -40,
    name: "Ragnar the Trader",
    tag: "trader",
    behaviour: {
        onTick(npc, ctx) { /* patrol, restock, schedule */ },
        onTalk(npc, ctx, player) { return ["Buy", "Sell", "Leave"]; }
    }
});

npcs.move(ragnar, 130, -38);
npcs.setState(ragnar, { stock: 12 });
const near = npcs.nearest(playerUid, 4);      // interaction helper

// client (presentation only, no authority)
npcs.onUpsert((npc) => npc.attachNameText({ offset: 2.2, cull: 15 }));
npcs.onTalkRequest((npc, options) => showChoiceList(options));   // future UI binding
```

### What this needs from the engine

1. `world.spawn` / `world.destroy` over existing prefabs (`ZNetScene.GetPrefab`,
   `ZDOMan.CreateNewZDO`) — P0, also useful without NPCs.
2. ZDO custom data read/write (`ZDO.GetString/Set`) so NPC identity and state survive saves.
3. A registry + snapshot-on-join, built entirely on `Rpc` (no new transport).
4. `npcs.*` binding: resolve ZDO → `GameObject`/`Character` via `ZNetScene.FindInstance(ZDOID)`,
   drive `MonsterAI`/`Talker`/`ZNetView` and dispatch to plugin handlers.
5. Client bundle helper for name text (`Chat.SetNpcText`) and, later, a choice-list UI binding.

### Limits to state up front

- Interaction is proximity + explicit trigger. The server cannot "see" an E-press on a
  non-networked object; for `creature` NPCs the game's own `Interactable` path can be used, for
  `visual` NPCs the client must send the intent (`Rpc`).
- Behaviour runs on the server tick (~1 Hz) plus event hooks — not a per-frame AI loop.
- No custom models: prefab must already exist in `ObjectDB`/`ZNetScene`.

## Dependency policy

Core runtime stays dependency-free beyond the script engine (Jint + Acornima): no framework is
required for plugins to load, talk, or touch the world.

For **content registration** the sensible option is the ecosystem's own framework rather than
reimplementing asset-bundle loading:

| Option | Cost | Verdict |
|---|---|---|
| Optional Jotunn integration (`com.jotunn.jotunn`) | none at load; reflection or a `SoftDependency` | **preferred** — expose `jotunn.*` bindings only when the plugin is present, mark them unavailable otherwise |
| Hard Jotunn dependency | load-order coupling, version churn, bigger install | no |
| Own bundle loader | code we must maintain forever | only if Jotunn integration proves impossible |

Rule of thumb: take a library when it owns a domain we would otherwise reimplement badly
(asset bundles, prefab registration); keep our own code for what we already do well and cheaply
(transport, plugin lifecycle, bindings). ServerSync-style config sync is unnecessary — the
inventory/state mirror and `Rpc` already cover it.

## P2 — content registration (needs a decision)

| Want | Reality |
|---|---|
| New item types | `ObjectDB` is built before the world loads; you can clone an existing prefab and register it, but you cannot invent art. Requires a registration window (pre-world) and prefab cloning |
| New prefabs (NPC models, props) | Must come from an `AssetBundle` (see EpicLoot `AssetBundleLoader`). Either (a) depend on Jotunn for registration, or (b) build a bundle loader binding |
| New recipes / stations / status effects | Doable via cloning + `ObjectDB` lists; same timing constraint as items |
| Custom client UI (quest panel) | Needs Unity UI code in the **client** bundle. A future `ui.panel()`/`ui.list()` declarative binding would cover most quest dialogs without exposing Unity |

## Hard limits (say no clearly)

- **No server-side rendering.** A dedicated server has no camera; anything visible must exist as a
  networked ZDO (already a prefab) or be drawn by a client bundle.
- **No new art from JS.** Models/textures need an asset bundle built in Unity.
- **No cancellation of damage yet.** `onDamage` observes after the fact; vetoing needs
  `Character.ApplyDamage` prefix support (deliberate: it changes combat semantics and must be
  opt-in per plugin).
- **No cross-session player identity.** The server gives a session uid; persistent identity keys
  off name + platform id, which the API should expose explicitly rather than let plugins guess.
- **Client-only data on the server is stale.** `players.list()` positions are reference positions
  and lag; do not drive hit detection from them.

## What a quest plugin looks like after P0/P1

```ts
const started = new Set<number>();

export default definePlugin({
    version: "1.0.0",
    onPlayerJoin: authorityHandler((ctx, player) => {
        const profile = ctx.host.api.players.profile(player.uid);   // P1
        if (!profile.done("intro")) {
            ctx.host.api.chat.sendTo(player.uid, "Speak to the trader to begin.");
        }
    }),
    onInteract: authorityHandler((ctx, event) => {                        // P0
        if (event.target !== "Trader_Ragnar") return;
        if (started.has(event.player)) return;
        started.add(event.player);
        ctx.host.api.player.message(event.player, "Bring me 10 wood.");  // P0
    }),
    onContainerChanged: authorityHandler((ctx, event) => {                // P0
        const wood = ctx.host.api.inventory.count(event.player, "Wood");
        if (wood >= 10) {
            ctx.host.api.inventory.remove(event.player, "Wood", 10);
            ctx.host.api.inventory.add(event.player, "Coins", 50);
            ctx.host.api.players.profile(event.player).set("intro", true);  // P1
        }
    })
});
```

That is the same shape as KG's quest mod, minus the UI and the custom NPC model.

## Recommended order

1. **P0 bindings + 4 events** — turns "policy plugin" into "gameplay plugin".
2. **Per-player profile store** — every quest/shop needs it immediately after P0.
3. **NPC registry + client renderer + dialog protocol** — the Marketplace-style experience.
4. **Content registration** — decide Jotunn dependency vs. own bundle loader before starting.
5. **Declarative client UI** — only if plugins demand more than NPC text/chat.

Each step is additive: a new `[JsBinder]` class, a capture point, regenerated declarations. The host
lifecycle and bridge are not expected to change until P2 content registration.
