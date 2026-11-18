# Changelog

## Unreleased

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
