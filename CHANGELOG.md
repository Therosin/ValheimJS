# Changelog

## Unreleased

- Planning: C# owns plan cursors, retries, timeouts, native action settlement, goal selection,
  cooldowns, preemption and utility selection/feedback/prerequisites. JS keeps goal callbacks,
  `PlanHandle`, and plugin actions.
- Behaviour goals: goals can declare `behaviour: { tree, state, action, condition, exec }`; the
  shared C# behaviour service instantiates and releases the tree. Life-demo no longer owns a tree
  lifecycle.
- Breaking: `new Plan(...)` is replaced by `ctx.run(...)` returning `PlanHandle`. The old JS plan
  loop and native action wrappers are removed; native `follow` and implicit `interact`/`harvest`
  approach remain host-owned plan semantics.
- NPC inventory: humanoid NPCs now open the vanilla `InventoryGui` container panel. The server
  remains authoritative; the client edits a mirror synchronized with native item serialization,
  and the NPC's body is frozen while the UI is open.
- Behaviour goals expose `inputs` and `state` for native cooking/fuel affordances; the life demo
  prefers cooked food, refuels empty fire pits, and withdraws wood when needed.
- NPC lifecycle: goal `commitMs` and `urgent` preemption semantics.
- Resource intents: bridge-authoritative reservations with per-actor ownership and TTL.
- Collection: `target-missing` / `not-loaded` revalidation, loading hold and retry.
- Hunting: crouch intent, prey scan, attack, loot window, look-around and stop reasons.
- Cooking: server-safe `CookingStation` adapter with board completion events.
- Storage: mixed material deposits, raw-food supply for cooking and access fallback.
- Feedback: hunt reward vs injury, duration and damage-event evaluation.
- Native NPC combat now yields to scripted movement/look/stop intent.

## Notes

- Resource intents are intentions, not native item ownership.
- `not-loaded` is distinct from contention and requires load-aware retry.
- Client BepInEx deployment must happen while the Valheim client is closed.
