---
title: Hook map
---

# Hook map

Mined from working mods (decompiled into `.local/mods`) to decide which capture points ValheimJS
should offer. Not modder-facing — see `guides/` for that.

## Sources

| Mod | Harmony patch attributes | Notes |
|---|---|---|
| EpicLoot | 457 | richest source: items, loot, crafting, RPC |
| KG Marketplace / Server NPCs | 235 | server NPCs, asset sync, terminal |
| Sakey's Equipment Slots | 73 | equipment/inventory |
| Innangard | 68 | building/placement |
| DvergerAutomation | 58 | automated containers |
| ChestHelper | 9 | container UI |
| FarLands | 0 | data/asset mod, no patches |

## Implemented

| Hook | Capture point | Why |
|---|---|---|
| `onChat` | `Chat.RPC_ChatMessage` **prefix** | observes routed chat arriving on this process before the game's async permission checks; private `!` commands use a separate channel |
| `onPlayerJoin` / `onPlayerLeave` | `ZNet.SendPlayerList` postfix + peer diff | the handshake (`RPC_PeerInfo`) returns early on bad password/full/version mismatch; diffing the published list only reports peers that actually made it |
| `onServerTick` | `ZNet.Update` postfix, ~1 Hz | shared patch on dedicated and client processes; use live authority checks before mutations |
| `onPlayerSpawned` / `onPlayerDespawned` | heartbeat poll over spawned character lookups | character readiness is distinct from peer connection |
| `onDamage` | `Character.Damage` postfix | read-only observation on the process receiving the call; this hook does not veto damage |
| Native private commands | `Chat.InputText` prefix (client-only) | consumes `!` input and submits it through `CommandService`; ordinary chat and `/` input continue normally |
| `game.*` | `ProcessPolicy` (executable/override), `ZNet.IsServer()` | side detection at startup and at runtime |

## Highest-value next candidates

| Target | Seen in | Use | Caveat |
|---|---|---|---|
| `Character.ApplyDamage` / `RPC_Damage` | 6 / 6 mods | cancellable damage (prefix `false`), PvP rules | `Damage` already applies by the time we see it; a prefix here can veto |
| `CharacterDrop.GenerateDropList` | 6 | loot tables, drop multipliers | runs per kill; must be cheap |
| `Humanoid.EquipItem` / `UnequipItem` | 12 / 6 | equipment restrictions, slot limits | also fires for clients' local prediction |
| `Projectile.OnHit` / `Setup` | 8 / 6 | projectile rules, ranged balance | fires per projectile hit |
| `Skills.RaiseSkill` | 6 | XP multipliers, skill caps | high frequency during activity |
| `ZDOMan.CreateNewZDO` | — | object spawn limits, logging | not patched by any mined mod; verify on the server build |
| `Player.OnSpawned` | 5 | per-spawn buffs, teleport-on-join | fires after the character exists |
| `ZNet.OnNewConnection` / `RPC_ServerHandshake` | KG | connection gating, allow-lists | handshake path is security-sensitive |
| `Terminal.InitTerminal` | 9 | console commands on both sides | client UI; not the server console |

## Avoid

- `InventoryGui.*`, `Hud.*`, `TextInput.*` — client UI. Meaningless headless, and the mined mods
  patch them only to draw things.
- `*::Awake` / `*::Update` of gameplay classes — per-frame or one-shot during scene load; the
  mined mods use them to grab singletons; `Container` can retain captured references when wired to appropriate hooks.
- Patching `UnityEngine.*` internals — not game code, breaks across versions.

## Selection rule

A hook earns a place in the framework when it is:

1. **authoritative** — runs on the process that decides the outcome (server), not just prediction;
2. **a funnel** — every relevant path goes through it (`Character.Damage`, not each `Attack.*`);
3. **cheap to observe** — a postfix that reads a few fields, no LINQ, no allocations per hit;
4. **shaped for JS** — payload is plain JSON data, not live object references.
