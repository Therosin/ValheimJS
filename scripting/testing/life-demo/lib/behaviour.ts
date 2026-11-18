/** Adapter types for the native `ai` binding. Kept small so the definitions are testable. */
export type BehaviorOutcome = "success" | "failure" | "running";

export interface BehaviorActionContext {
    args: Record<string, unknown>;
    state: Record<string, unknown>;
    this: unknown;
    deltaTime: number;
    interrupted: boolean;
}

export interface BehaviorHandlers {
    action: (name: string, ctx: BehaviorActionContext) => BehaviorOutcome;
    condition: (name: string, ctx: BehaviorActionContext) => boolean;
}

export interface BehaviorRuntime {
    instantiate(
        treeId: string,
        options: BehaviorHandlers & { this?: unknown; state?: Record<string, unknown> }
    ): string;
    tick(instanceId: string, deltaTime: number): BehaviorOutcome | "cancelled";
    release(instanceId: string): void;
}

/** NPC host cadence. */
export const TICK_SECONDS = 0.5;
