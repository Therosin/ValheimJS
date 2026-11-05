import { context } from "./runtime";

/** Thin accessors over host-installed API objects. No state, no registries. */

/** Process/world identity and live snapshots. */
export function game() {
    return context().host.api.game;
}

/** Live world objects: counts, spawning and per-object data. */
export function world() {
    return context().host.api.world;
}

/** Players: local identity, lookups and the authoritative online roster. */
export function players() {
    return context().host.api.players;
}

/** This plugin's sandboxed data folder. */
export function fs() {
    return context().host.api.fs;
}
