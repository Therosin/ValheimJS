import { context } from "./runtime";
import type {
    AiActionOutcome,
    AiContext,
    AiInstantiateOptions,
    AiLoadOptions,
    AiStatus,
    AiTickResult
} from "./api/types";

export type { AiActionOutcome, AiContext, AiInstantiateOptions, AiLoadOptions, AiStatus, AiTickResult } from "./api/types";

/**
 * YAML behaviour trees. Compile once, then create one instance per entity and tick it on the
 * NPC cadence. When `this` has an `id` (a `ValheimBaseNPC`), the host also injects the NPC
 * perception store as `ctx.state.senses` before each tick.
 *
 * ```ts
 * function makeBrain(entity: ValheimBaseNPC, tree: string) {
 *     return ai.instantiate(tree, {
 *         this: entity,
 *         action: (name, c) => name === "move"
 *             ? (entity.moveTo(5, entity.y, 0, false) ? "running" : "failure")
 *             : "success",
 *         condition: (name) => name === "hurt" && entity.health < 10
 *     });
 * }
 * ```
 *
 * `ctx.state` is copied into JS for each callback and written back afterwards, so
 * `ctx.state.attempts = Number(ctx.state.attempts) + 1` works as authored. See
 * `docs/guides/npc-ai.md` for a complete per-NPC example.
 */
export const ai = {
    /** Compiles YAML text; imports come from `options.imports` keyed by alias, never from disk. */
    compile(yaml: string, options: AiLoadOptions = {}): string {
        return context().host.api.ai.compile(yaml, options);
    },
    /** Creates an independent execution state for a compiled tree. */
    instantiate(tree: string, options: AiInstantiateOptions = {}): string {
        return context().host.api.ai.instantiate(tree, options) as string;
    },
    /** Advances one instance. Defaults to a 50 ms step; NPCs should pass their tick interval. */
    tick(instance: string, deltaTime = 0.05): AiTickResult {
        return context().host.api.ai.tick(instance, deltaTime) as AiTickResult;
    },
    /** Deep copy of the instance state. */
    state(instance: string): Record<string, unknown> {
        return context().host.api.ai.state(instance) as Record<string, unknown>;
    },
    /** Replaces one top-level state value. */
    set(instance: string, key: string, value: unknown): void {
        context().host.api.ai.set(instance, key, value);
    },
    status(instance: string): AiStatus {
        return context().host.api.ai.status(instance) as AiStatus;
    },
    /** Stops the instance; active `execAfter` hooks run with `interrupted: true`. */
    cancel(instance: string): void {
        context().host.api.ai.cancel(instance);
    },
    /** Clears execution progress; `resetState` restores declared defaults. */
    reset(instance: string, resetState = false): void {
        context().host.api.ai.reset(instance, resetState);
    },
    /** Drops one instance without running hooks. */
    release(instance: string): boolean {
        return context().host.api.ai.release(instance);
    },
    /** Drops a compiled tree and every instance using it. */
    unload(tree: string): boolean {
        return context().host.api.ai.unload(tree);
    }
};
