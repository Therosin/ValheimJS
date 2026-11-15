import type { NpcAffordances, NpcInteractOptions, NpcInteractionResult, NpcSensed, NpcView } from "../api/types";

/**
 * Action registry for `Plan` steps.
 *
 * Built-in actions live in `sdk/actions/*` and register here. Plugins can register their own with
 * `registerAction` and use them as `{ action: "name", config: ... }` steps. The planner only
 * looks names up; it has no knowledge of what an action does.
 */

export interface ActionResultLike {
    ok: boolean;
    reason?: string | null;
}

export interface PlannerActor {
    moveTo(x: number, y: number, z: number, run: boolean): ActionResultLike;
    idle(): ActionResultLike;
    stop(): ActionResultLike;
    wait(seconds: number): ActionResultLike;
    lookAt(x: number, y: number, z: number): ActionResultLike;
    face(id: string): ActionResultLike;
    pick(id: string): ActionResultLike;
    interact?(id: string, options?: NpcInteractOptions): NpcInteractionResult;
    affordances?(id: string): NpcAffordances;
    attack?(id: string, secondary?: boolean): ActionResultLike;
    jump?(): ActionResultLike;
}

export type Vec3Like = { x: number; y: number; z: number } | [number, number, number];

export function position(value: Vec3Like): { x: number; y: number; z: number } {
    return Array.isArray(value) ? { x: value[0], y: value[1], z: value[2] } : value;
}

export type ActionStatus = "running" | "success" | "failure";

export type ActionOutcome =
    | ActionStatus
    | { status: ActionStatus; reason?: string };

export interface ActionSettlement {
    kind: string;
    status: "arrived" | "failed" | "cancelled";
    reason: string | null;
}

export interface ActionContext {
    readonly view: NpcView;
    readonly actor: PlannerActor;
    readonly now: number;
    /** Plan-wide scratch data. */
    readonly state: Record<string, unknown>;
    /** Per-step scratch data; reset when the step advances or retries. */
    readonly local: Record<string, unknown>;
    /** True while a native action issued through `issue` has not settled. */
    readonly awaiting: boolean;
    log(message: string): void;
    sense(id: string): NpcSensed | null;
    /**
     * Issues one native action and tracks it. Returns the immediate acceptance result; settlement
     * is read with `takeSettled`.
     */
    issue(kind: string, issue: () => ActionResultLike | undefined): ActionResultLike | undefined;
    /** Last terminal action result, consumed once. */
    takeSettled(): ActionSettlement | null;
}

export interface ActionHandler<TConfig = unknown> {
    tick(context: ActionContext, config: TConfig): ActionOutcome;
}

export class ActionRegistry {
    private readonly handlers = new Map<string, ActionHandler<unknown>>();

    register<TConfig>(name: string, handler: ActionHandler<TConfig>): void {
        if (name.trim().length === 0) {
            throw new Error("action name must not be empty");
        }

        this.handlers.set(name, handler as ActionHandler<unknown>);
    }

    unregister(name: string): boolean {
        return this.handlers.delete(name);
    }

    has(name: string): boolean {
        return this.handlers.has(name);
    }

    get(name: string): ActionHandler<unknown> | undefined {
        return this.handlers.get(name);
    }

    names(): string[] {
        return [...this.handlers.keys()];
    }
}

/** Default registry used by `Plan` and `Planner`. */
export const actions = new ActionRegistry();

/** Registers a plugin action in the default registry. */
export function registerAction<TConfig>(name: string, handler: ActionHandler<TConfig>): void {
    actions.register(name, handler);
}
