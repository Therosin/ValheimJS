import type { NpcPos, NpcSensed, NpcView } from "../../../sdk/api/types";

/** Activity names are also the AI tree keys and the manual/LLM request vocabulary. */
export type VillagerActivity =
    | "wander"
    | "inspect"
    | "socialize"
    | "forage"
    | "seekHome"
    | "return"
    | "rest"
    | "flee";

/** The NPC surface the YAML actions ultimately drive. */
export interface VillagerActor {
    moveTo(x: number, y: number, z: number, run: boolean): boolean;
    wait(seconds: number): boolean;
    lookAt(x: number, y: number, z: number): boolean;
    face(id: string): boolean;
    idle(): boolean;
}

export interface VillagerNeeds {
    energy: number;
    social: number;
    curiosity: number;
}

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

/**
 * Adapter over the native `ai` binding. Injecting it keeps brain selection testable without Jint;
 * the real plugin passes `ai.instantiate`, `ai.tick`, `ai.release`.
 */
export interface BehaviorRuntime {
    instantiate(treeId: string, options: BehaviorHandlers & { this?: unknown; state?: Record<string, unknown> }): string;
    tick(instanceId: string, deltaTime: number): BehaviorOutcome | "cancelled";
    release(instanceId: string): void;
}

export interface VillagerBrainOptions {
    actor: VillagerActor;
    runtime: BehaviorRuntime;
    behaviors: Record<VillagerActivity, string>;
    home: NpcPos;
    memory?: Record<string, unknown>;
    needs?: Partial<VillagerNeeds>;
    random?: () => number;
    log?: (message: string) => void;
}

const clamp01 = (value: number): number => Math.max(0, Math.min(1, value));
const distance = (a: NpcPos, b: NpcPos): number => Math.hypot(a.x - b.x, a.z - b.z);
const numberOr = (value: unknown, fallback: number): number =>
    typeof value === "number" && Number.isFinite(value) ? value : fallback;
const stringOr = (value: unknown, fallback: string): string => (typeof value === "string" ? value : fallback);
const isPosition = (value: unknown): value is NpcPos => {
    if (!value || typeof value !== "object") return false;
    const pos = value as Partial<NpcPos>;
    return Number.isFinite(pos.x) && Number.isFinite(pos.y) && Number.isFinite(pos.z);
};

const COOLDOWN_SECONDS: Record<VillagerActivity, readonly [number, number]> = {
    wander: [2, 5],
    inspect: [20, 40],
    socialize: [25, 50],
    forage: [20, 45],
    seekHome: [8, 15],
    return: [1, 3],
    rest: [3, 8],
    flee: [10, 20]
};

/**
 * The brain gives rough direction: needs, utility selection, interruption and remembered facts.
 * It never expresses the internal sequence of an activity. That lives in the YAML trees under
 * `behaviours/`, which compose micro trees such as `move`, `wait`, `look`, `face` and `claim`.
 * The action handlers below are generic primitives (select/gesture/intent); an activity tree
 * decides when to call them.
 */
export class VillagerBrain {
    readonly needs: VillagerNeeds;

    private readonly actor: VillagerActor;
    private readonly runtime: BehaviorRuntime;
    private readonly behaviors: Record<VillagerActivity, string>;
    private readonly random: () => number;
    private readonly log?: (message: string) => void;
    private readonly visited: Set<string>;
    private readonly blocked: Map<string, number>;
    private readonly cooldownUntil: Record<VillagerActivity, number> = {
        wander: 0,
        inspect: 0,
        socialize: 0,
        forage: 0,
        seekHome: 0,
        return: 0,
        rest: 0,
        flee: 0
    };

    private activity: VillagerActivity = "wander";
    private instance: string | null = null;
    private activeTarget: NpcSensed | null = null;
    private restSeconds = 20;
    private lastTick = 0;
    private dirty = false;
    private home: NpcPos;
    private sheltered = false;
    private memory: Record<string, unknown>;
    private requested: { activity: VillagerActivity; target?: NpcSensed; until: number } | null = null;
    private homesick = false;

    constructor(options: VillagerBrainOptions) {
        this.actor = options.actor;
        this.runtime = options.runtime;
        this.behaviors = options.behaviors;
        this.random = options.random ?? Math.random;
        this.log = options.log;
        this.memory = options.memory ?? {};
        this.home = isPosition(this.memory.home) ? this.memory.home : options.home;
        this.sheltered = typeof this.memory.shelter === "string";
        this.needs = {
            energy: clamp01(numberOr(options.needs?.energy, numberOr(this.memory.energy, 0.75))),
            social: clamp01(numberOr(options.needs?.social, numberOr(this.memory.social, 0.4))),
            curiosity: clamp01(numberOr(options.needs?.curiosity, numberOr(this.memory.curiosity, 0.55)))
        };
        this.visited = new Set(
            Array.isArray(this.memory.visited)
                ? this.memory.visited.filter((entry): entry is string => typeof entry === "string")
                : []
        );
        this.blocked = new Map(
            this.memory.blocked && typeof this.memory.blocked === "object"
                ? Object.entries(this.memory.blocked as Record<string, unknown>).filter(
                      (entry): entry is [string, number] => typeof entry[1] === "number"
                  )
                : []
        );
    }

    get current(): VillagerActivity {
        return this.activity;
    }

    get isDirty(): boolean {
        return this.dirty;
    }

    get isSheltered(): boolean {
        return this.sheltered;
    }

    get homePosition(): NpcPos {
        return this.home;
    }

    get hasInstance(): boolean {
        return this.instance !== null;
    }

    snapshotMemory(): Record<string, unknown> {
        return {
            ...this.memory,
            home: this.home,
            ...(this.sheltered ? { shelter: this.memory.shelter } : {}),
            energy: this.needs.energy,
            social: this.needs.social,
            curiosity: this.needs.curiosity,
            visited: [...this.visited].slice(-64),
            blocked: Object.fromEntries([...this.blocked].slice(-64))
        };
    }

    clearDirty(): void {
        this.dirty = false;
    }

    /** Manual/LLM seam: cancel the current tree and run one activity next tick. */
    request(activity: VillagerActivity, target?: NpcSensed | null, now = Date.now()): void {
        if (this.instance) {
            this.runtime.release(this.instance);
            this.instance = null;
        }
        this.requested = { activity, target: target ?? undefined, until: now + 30_000 };
    }

    /** One NPC tick. The brain ticks the active YAML instance or selects a new activity. */
    tick(view: NpcView, now = Date.now()): VillagerActivity {
        const elapsed = this.lastTick > 0 ? Math.min(5, (now - this.lastTick) / 1000) : 0;
        this.lastTick = now;
        this.updateNeeds(view, elapsed);

        if (this.instance) {
            const status = this.runtime.tick(this.instance, elapsed);
            if (status === "running") return this.activity;

            const finished = this.activity;
            this.runtime.release(this.instance);
            this.instance = null;
            if (status === "success") this.complete(now);
            else this.abandon(now);
            return finished;
        }

        const threat = this.threat(view);
        if (threat) {
            this.start("flee", now, threat);
            return this.activity;
        }

        if (this.requested && now <= this.requested.until) {
            const request = this.requested;
            this.requested = null;
            this.start(request.activity, now, request.target ?? null);
            return this.activity;
        }

        const decision = this.choose(view, now);
        if (decision) this.start(decision.activity, now, decision.target ?? null);
        else this.pause(now);
        return this.activity;
    }

    /** Cancel/release on despawn or plugin unload. */
    dispose(): void {
        if (this.instance) {
            this.runtime.release(this.instance);
            this.instance = null;
        }
    }

    private choose(view: NpcView, now: number): { activity: VillagerActivity; target?: NpcSensed } | null {
        const night = view.environment ? !view.environment.isDay : false;
        const away = distance(view, this.home);
        const player = nearest(view.nearby, entry => entry.isPlayer && entry.visible && !entry.isDead && entry.distance <= 18);
        const object = nearest(
            view.objects,
            entry =>
                entry.visible &&
                !entry.isDead &&
                !this.visited.has(entry.id) &&
                (this.blocked.get(entry.id) ?? 0) <= now &&
                entry.distance <= 14
        );
        const decisions: Array<{ activity: VillagerActivity; score: number; target?: NpcSensed }> = [];

        if (this.needs.energy < 0.25) decisions.push({ activity: "rest", score: 800 });
        else if (night) decisions.push({ activity: "rest", score: 500 + (1 - this.needs.energy) * 200 });

        if (away > 25) decisions.push({ activity: "return", score: 700 });
        else if (this.homesick && away > 8) decisions.push({ activity: "return", score: 650 });
        else if (night && away > 8) decisions.push({ activity: "return", score: 450 });
        else if (this.needs.energy < 0.35 && away > 10) decisions.push({ activity: "return", score: 350 });

        if (!this.sheltered && this.ready("seekHome", now) && this.findShelter(view)) {
            decisions.push({ activity: "seekHome", score: 420 });
        }

        if (player && this.needs.social > 0.55 && this.ready("socialize", now)) {
            decisions.push({ activity: "socialize", score: 300 + this.needs.social * 100, target: player });
        }

        if (object && this.needs.curiosity > 0.55 && this.ready("inspect", now)) {
            decisions.push({ activity: "inspect", score: 220 + this.needs.curiosity * 80, target: object });
        }

        if (this.ready("forage", now) && this.needs.curiosity > 0.5 && this.needs.energy > 0.35) {
            decisions.push({ activity: "forage", score: (night ? 60 : 160) + this.needs.curiosity * 80 });
        }

        if (this.ready("wander", now)) decisions.push({ activity: "wander", score: 10 + (night ? 0 : 10) });

        return decisions.sort((left, right) => right.score - left.score)[0] ?? null;
    }

    private start(activity: VillagerActivity, now: number, target: NpcSensed | null): void {
        const tree = this.behaviors[activity];
        if (!tree) {
            this.log?.(`missing behaviour tree for ${activity}`);
            this.cooldownUntil[activity] = now + 5000;
            return;
        }

        this.activity = activity;
        this.activeTarget = target;
        this.restSeconds = 12 + this.random() * 18;
        this.log?.(`${activity}${target ? ` -> ${target.name || target.kind || target.id}` : ""}`);

        const state: Record<string, unknown> = {
            home: this.home,
            target: target ?? null,
            destination: null,
            run: false,
            moveIssued: false,
            waitIssued: false
        };
        if (activity === "rest") state.waitSeconds = this.restSeconds;

        this.instance = this.runtime.instantiate(tree, {
            this: this.actor,
            state,
            action: (name, context) => this.action(name, context),
            condition: (name, context) => this.condition(name, context)
        });
    }

    private complete(now: number): void {
        const activity = this.activity;
        this.activeTarget = null;
        this.dirty = true;

        if (activity === "rest") this.needs.energy = clamp01(this.needs.energy + 0.35);
        else if (activity === "socialize") this.needs.social = clamp01(this.needs.social - 0.4);
        else if (activity === "inspect") this.needs.curiosity = clamp01(this.needs.curiosity - 0.35);
        else if (activity === "forage") {
            this.needs.curiosity = clamp01(this.needs.curiosity - 0.25);
            this.homesick = true;
        } else if (activity === "return") this.homesick = false;
        else if (activity === "flee") this.needs.energy = clamp01(this.needs.energy - 0.05);

        const [minimum, maximum] = COOLDOWN_SECONDS[activity];
        this.cooldownUntil[activity] = now + (minimum + this.random() * (maximum - minimum)) * 1000;
    }

    private abandon(now: number): void {
        this.log?.(`${this.activity} failed`);
        if (this.activity === "inspect" && this.activeTarget) {
            this.blocked.set(this.activeTarget.id, now + 5 * 60 * 1000);
            this.dirty = true;
        }
        const activity = this.activity;
        this.activeTarget = null;
        if (activity === "forage") this.cooldownUntil[activity] = now + 20_000 + this.random() * 20_000;
        else this.cooldownUntil[activity] = now + 3000 + this.random() * 3000;
    }

    private pause(now: number): void {
        this.activity = "wander";
        this.cooldownUntil.wander = now + 1000;
    }

    private updateNeeds(view: NpcView, elapsed: number): void {
        if (elapsed <= 0) return;
        const moving = view.intent?.kind === "moveTo" && view.intent.status === "running";
        const resting = this.activity === "rest" && this.instance !== null;
        const playerNearby = view.nearby.some(entry => entry.isPlayer && !entry.isDead && entry.distance <= 12);

        this.needs.energy = clamp01(
            this.needs.energy + (resting ? 0.04 : moving ? -0.006 : -0.0015) * elapsed
        );
        this.needs.social = clamp01(this.needs.social + (playerNearby ? 0.012 : 0.004) * elapsed);
        this.needs.curiosity = clamp01(this.needs.curiosity + 0.005 * elapsed);
    }

    private threat(view: NpcView): NpcSensed | null {
        return nearest(view.nearby, entry => entry.enemy && entry.visible && !entry.isDead && entry.distance <= 18);
    }

    private findShelter(view: NpcView): NpcSensed | null {
        if (this.sheltered) return null;
        return (
            view.objects
                .filter(entry => entry.visible && !entry.isDead && (entry.kind === "bed" || entry.kind === "door"))
                .find(entry =>
                    view.nearby.every(
                        other =>
                            !other.isPlayer ||
                            Math.hypot(other.pos.x - entry.pos.x, other.pos.z - entry.pos.z) > 12
                    )
                ) ?? null
        );
    }

    private ready(activity: VillagerActivity, now: number): boolean {
        return this.cooldownUntil[activity] <= now;
    }

    private condition(name: string, ctx: BehaviorActionContext): boolean {
        if (name !== "at-home") return false;
        const view = ctx.state.senses as NpcView | undefined;
        if (!view) return false;
        return distance(view, this.home) <= 2.5;
    }

    private action(name: string, ctx: BehaviorActionContext): BehaviorOutcome {
        const state = ctx.state;
        const view = state.senses as NpcView | undefined;

        if (name === "select-point") {
            if (!view) return "failure";
            const around = stringOr(ctx.args.around, "home") === "self" ? view : this.home;
            const point = this.pointAround(around, view.y, numberOr(ctx.args.min, 3), numberOr(ctx.args.max, 8));
            state.destination = point;
            state.run = ctx.args.run === true;
            state.moveIssued = false;
            return "success";
        }

        if (name === "select-home") {
            state.destination = this.home;
            state.run = false;
            state.moveIssued = false;
            return "success";
        }

        if (name === "select-target") {
            return this.selectTarget(ctx, state, view);
        }

        if (name === "select-shelter") {
            if (!view || this.sheltered) return "failure";
            const shelter = this.findShelter(view);
            if (!shelter) return "failure";
            this.activeTarget = shelter;
            state.target = shelter;
            state.destination = shelter.pos;
            state.moveIssued = false;
            return "success";
        }

        if (name === "select-flee-point") {
            if (!view) return "failure";
            const threat = this.threat(view);
            if (!threat) return "failure";
            this.activeTarget = threat;
            state.target = threat;
            state.destination = this.fleePoint(view, threat);
            state.run = true;
            state.moveIssued = false;
            return "success";
        }

        if (name === "move-to") {
            return this.moveAction(state, view);
        }

        if (name === "wait") {
            return this.waitAction(ctx, state, view);
        }

        if (name === "look-at") {
            const target = state.target as NpcSensed | undefined;
            if (!target) return "failure";
            return this.actor.lookAt(target.pos.x, target.pos.y, target.pos.z) ? "success" : "failure";
        }

        if (name === "look-around") {
            if (!view) return "failure";
            const point = this.pointAround(view, view.y, 2, 5);
            return this.actor.lookAt(point.x, point.y, point.z) ? "success" : "failure";
        }

        if (name === "face") {
            const target = state.target as NpcSensed | undefined;
            if (!target) return "failure";
            return this.actor.face(target.id) ? "success" : "failure";
        }

        if (name === "claim-home") {
            const target = state.target as NpcSensed | undefined;
            if (!target) return "failure";
            this.home = target.pos;
            this.sheltered = true;
            this.memory.home = this.home;
            this.memory.shelter = target.id;
            this.dirty = true;
            this.log?.(`claimed shelter ${target.name || target.id}`);
            return "success";
        }

        if (name === "mark-visited") {
            const target = state.target as NpcSensed | undefined;
            if (!target) return "failure";
            this.visited.add(target.id);
            this.dirty = true;
            return "success";
        }

        if (name === "remember-meeting") {
            const target = state.target as NpcSensed | undefined;
            if (!target) return "failure";
            this.memory.lastMet = { id: target.id, name: target.name, at: Date.now() };
            this.dirty = true;
            return "success";
        }

        return "failure";
    }

    private selectTarget(ctx: BehaviorActionContext, state: Record<string, unknown>, view: NpcView | undefined): BehaviorOutcome {
        if (!view) return "failure";
        const kind = stringOr(ctx.args.kind, "any");
        const maxDistance = numberOr(ctx.args.maxDistance, 20);
        const source = kind === "object" ? view.objects : view.nearby;
        const target = nearest(
            source,
            entry =>
                entry.visible &&
                !entry.isDead &&
                entry.distance <= maxDistance &&
                (kind !== "player" || entry.isPlayer) &&
                (kind !== "enemy" || entry.enemy) &&
                (kind !== "object" || entry.isObject) &&
                (entry.isObject ? !this.visited.has(entry.id) && (this.blocked.get(entry.id) ?? 0) <= Date.now() : true)
        );
        if (!target) return "failure";
        this.activeTarget = target;
        state.target = target;
        state.destination = target.pos;
        state.moveIssued = false;
        return "success";
    }

    private moveAction(state: Record<string, unknown>, view: NpcView | undefined): BehaviorOutcome {
        if (!view) return "failure";
        if (state.moveIssued === true) {
            const intent = view.intent;
            if (intent?.kind === "moveTo") {
                if (intent.status === "arrived") return "success";
                if (intent.status === "failed") return "failure";
                return "running";
            }
            return "running";
        }

        const destination = state.destination as NpcPos | undefined;
        if (!isPosition(destination)) return "failure";
        if (!this.actor.moveTo(destination.x, destination.y, destination.z, state.run === true)) return "failure";
        state.moveIssued = true;
        return "running";
    }

    private waitAction(ctx: BehaviorActionContext, state: Record<string, unknown>, view: NpcView | undefined): BehaviorOutcome {
        if (!view) return "failure";
        if (state.waitIssued === true) {
            const intent = view.intent;
            if (intent?.kind === "wait") {
                if (intent.status === "arrived") return "success";
                if (intent.status === "failed") return "failure";
                return "running";
            }
            return "running";
        }

        const seconds = numberOr(ctx.args.seconds, numberOr(state.waitSeconds, 2));
        if (!this.actor.wait(seconds)) return "failure";
        state.waitIssued = true;
        return "running";
    }

    private pointAround(origin: NpcPos, y: number, minimum: number, maximum: number): NpcPos {
        const angle = this.random() * Math.PI * 2;
        const radius = minimum + this.random() * (maximum - minimum);
        return {
            x: origin.x + Math.cos(angle) * radius,
            y,
            z: origin.z + Math.sin(angle) * radius
        };
    }

    private fleePoint(view: NpcView, threat: NpcSensed): NpcPos {
        const dx = view.x - threat.pos.x;
        const dz = view.z - threat.pos.z;
        const length = Math.hypot(dx, dz) || 1;
        return { x: view.x + (dx / length) * 10, y: view.y, z: view.z + (dz / length) * 10 };
    }
}

function nearest(entries: NpcSensed[], predicate: (entry: NpcSensed) => boolean): NpcSensed | null {
    let best: NpcSensed | null = null;
    for (const entry of entries) {
        if (!predicate(entry)) continue;
        if (!best || entry.distance < best.distance) best = entry;
    }
    return best;
}
