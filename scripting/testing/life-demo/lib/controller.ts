import type { NpcPos, NpcSensed, NpcView } from "../../../sdk/api/types";

/**
 * What the villager is doing. These are also the names a manual operator or an LLM planner can
 * pass to `request()`.
 */
export type VillagerActivity =
    | "wander"
    | "inspect"
    | "socialize"
    | "forage"
    | "seekHome"
    | "return"
    | "rest"
    | "flee";

/** Subset of the native NPC surface the brain needs. Keeps the controller unit-testable. */
export interface VillagerActor {
    moveTo(x: number, y: number, z: number, run: boolean): boolean;
    wait(seconds: number): boolean;
    lookAt(x: number, y: number, z: number): boolean;
    face(id: string): boolean;
    idle(): boolean;
}

export interface VillagerNeeds {
    /** 0 tired .. 1 rested. Sleep restores it; moving drains it faster. */
    energy: number;
    /** 0 content .. 1 lonely. Time and nearby players raise it; socializing lowers it. */
    social: number;
    /** 0 satisfied .. 1 bored. Time raises it; inspecting and foraging lower it. */
    curiosity: number;
}

export interface VillagerControllerOptions {
    actor: VillagerActor;
    /** Spawn point; persistent memory can override it. */
    home: NpcPos;
    memory?: Record<string, unknown>;
    /** Test/debug overrides. */
    needs?: Partial<VillagerNeeds>;
    random?: () => number;
    log?: (message: string) => void;
}

type Pending = "move" | "look" | "wait";
type Outcome = "running" | "success" | "failure";

interface Decision {
    activity: VillagerActivity;
    score: number;
    target?: NpcSensed;
    point?: NpcPos;
}

const clamp01 = (value: number): number => Math.max(0, Math.min(1, value));
const distance = (a: NpcPos, b: NpcPos): number => Math.hypot(a.x - b.x, a.z - b.z);
const numberOr = (value: unknown, fallback: number): number =>
    typeof value === "number" && Number.isFinite(value) ? value : fallback;
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
 * Central brain: a utility scorer picks one simple behaviour at a time, and each behaviour runs
 * to a settled host intent before the brain picks again. Nothing here scripts the world directly;
 * the NPC only ever receives move/wait/look/face intents, so the native simulation stays in
 * charge and the same activity names can be driven by an operator or planner later.
 */
export class VillagerController {
    readonly needs: VillagerNeeds;

    private readonly actor: VillagerActor;
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
    private pending: Pending | null = null;
    private target: NpcSensed | null = null;
    private restSeconds = 20;
    private lastTick = 0;
    private dirty = false;
    private home: NpcPos;
    private sheltered = false;
    private memory: Record<string, unknown>;
    private requested: Decision | null = null;
    private requestedUntil = 0;
    private homesick = false;

    constructor(options: VillagerControllerOptions) {
        this.actor = options.actor;
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
                ? Object.entries(this.memory.blocked as Record<string, unknown>)
                      .filter((entry): entry is [string, number] => typeof entry[1] === "number")
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

    /** Persistent data for `npc.setMemory`; unknown keys from earlier versions are preserved. */
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

    /**
     * Manual override for an operator or a future LLM planner. The activity runs once, then normal
     * utility selection resumes.
     */
    request(activity: VillagerActivity, target?: NpcSensed | null, now = Date.now()): void {
        this.requested = { activity, score: Number.POSITIVE_INFINITY, target: target ?? undefined };
        this.requestedUntil = now + 30_000;
    }

    /** One NPC tick. Returns the activity being pursued. */
    tick(view: NpcView, now = Date.now()): VillagerActivity {
        const elapsed = this.lastTick > 0 ? Math.min(5, (now - this.lastTick) / 1000) : 0;
        this.lastTick = now;
        this.updateNeeds(view, elapsed);

        if (this.pending) {
            const outcome = this.advance(view);
            if (outcome === "success") this.complete(now);
            else if (outcome === "failure") this.abandon(now);
            return this.activity;
        }

        const threat = this.threat(view);
        if (threat) {
            this.begin({ activity: "flee", score: 1000, target: threat }, view, now);
            return this.activity;
        }

        if (this.requested && now <= this.requestedUntil) {
            const request = this.requested;
            this.requested = null;
            this.begin(request, view, now);
            return this.activity;
        }

        const decision = this.choose(view, now);
        if (decision) this.begin(decision, view, now);
        else this.pause(now);
        return this.activity;
    }

    private choose(view: NpcView, now: number): Decision | null {
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
        const shelter = this.findShelter(view);
        const decisions: Decision[] = [];

        if (this.needs.energy < 0.25) {
            decisions.push({ activity: "rest", score: 800 });
        } else if (night) {
            decisions.push({ activity: "rest", score: 500 + (1 - this.needs.energy) * 200 });
        }

        if (away > 25) {
            decisions.push({ activity: "return", score: 700 });
        } else if (this.homesick && away > 8) {
            decisions.push({ activity: "return", score: 650 });
        } else if (night && away > 8) {
            decisions.push({ activity: "return", score: 450 });
        } else if (this.needs.energy < 0.35 && away > 10) {
            decisions.push({ activity: "return", score: 350 });
        }

        if (shelter && !this.sheltered && this.ready("seekHome", now)) {
            decisions.push({ activity: "seekHome", score: 420, target: shelter });
        } else if (!this.sheltered && !shelter && night && this.ready("seekHome", now)) {
            decisions.push({ activity: "seekHome", score: 180 });
        }

        if (player && this.needs.social > 0.55 && this.ready("socialize", now)) {
            decisions.push({ activity: "socialize", score: 300 + this.needs.social * 100, target: player });
        }

        if (object && this.needs.curiosity > 0.55 && this.ready("inspect", now)) {
            decisions.push({ activity: "inspect", score: 220 + this.needs.curiosity * 80, target: object });
        }

        if (this.ready("forage", now) && this.needs.curiosity > 0.5 && this.needs.energy > 0.35) {
            const score = (night ? 60 : 160) + this.needs.curiosity * 80;
            decisions.push({ activity: "forage", score });
        }

        if (this.ready("wander", now)) {
            decisions.push({ activity: "wander", score: 10 + (night ? 0 : 10) });
        }

        return decisions.sort((left, right) => right.score - left.score)[0] ?? null;
    }

    private begin(decision: Decision, view: NpcView, now: number): void {
        const { activity, target, point } = decision;
        this.activity = activity;
        this.target = target ?? null;
        this.pending = null;
        this.restSeconds = 12 + this.random() * 18;
        this.log?.(
            `${activity}${target ? ` -> ${target.name || target.kind || target.id}` : ""}` +
                `${point ? ` @ ${Math.round(point.x)}, ${Math.round(point.z)}` : ""}`
        );

        if (activity === "wander") {
            const destination = this.wanderPoint(view, 3, 8);
            if (this.actor.moveTo(destination.x, destination.y, destination.z, false)) this.pending = "move";
        } else if (activity === "forage") {
            const destination = point ?? this.wanderPoint(view, 10, 18);
            if (this.actor.moveTo(destination.x, destination.y, destination.z, true)) this.pending = "move";
        } else if (activity === "return") {
            if (this.actor.moveTo(this.home.x, this.home.y, this.home.z, false)) this.pending = "move";
        } else if (activity === "rest") {
            if (distance(view, this.home) > 2.5) {
                if (this.actor.moveTo(this.home.x, this.home.y, this.home.z, false)) this.pending = "move";
            } else if (this.actor.wait(this.restSeconds)) {
                this.pending = "wait";
            }
        } else if (activity === "seekHome") {
            const destination = target?.pos ?? this.wanderPoint(view, 10, 20);
            if (this.actor.moveTo(destination.x, destination.y, destination.z, false)) this.pending = "move";
        } else if (activity === "flee") {
            const destination = target ? this.fleePoint(view, target) : this.home;
            if (this.actor.moveTo(destination.x, destination.y, destination.z, true)) this.pending = "move";
        } else if (target) {
            if (this.actor.moveTo(target.pos.x, target.pos.y, target.pos.z, false)) this.pending = "move";
        }

        if (!this.pending) {
            this.cooldownUntil[activity] = now + 4000;
            this.log?.(`${activity} unavailable`);
        }
    }

    private advance(view: NpcView): Outcome {
        const intent = view.intent;
        if (!intent || intent.status === "idle") return "running";
        if (intent.status === "failed") return "failure";
        if (intent.status === "running") return "running";
        if (this.pending === "move") return this.onArrive() ? "running" : "failure";
        if (this.pending === "look") {
            if (!this.actor.wait(1.5 + this.random() * 2)) return "failure";
            this.pending = "wait";
            return "running";
        }
        return "success";
    }

    private onArrive(): boolean {
        if (this.activity === "wander") {
            if (!this.actor.wait(1.5 + this.random() * 2.5)) return false;
            this.pending = "wait";
            return true;
        }

        if (this.activity === "forage") {
            const origin = this.target?.pos ?? this.home;
            const look = this.pointAround(origin, 2, 5, origin.y);
            if (!this.actor.lookAt(look.x, look.y, look.z)) return false;
            this.pending = "look";
            return true;
        }

        if (this.activity === "return") {
            if (!this.actor.wait(1 + this.random() * 2)) return false;
            this.pending = "wait";
            return true;
        }

        if (this.activity === "seekHome") {
            if (this.target && !this.sheltered) {
                this.home = this.target.pos;
                this.sheltered = true;
                this.memory.shelter = this.target.id;
                this.memory.home = this.home;
                this.dirty = true;
                this.log?.(`claimed shelter ${this.target.name || this.target.id}`);
            }
            if (!this.actor.lookAt(this.home.x, this.home.y, this.home.z)) return false;
            this.pending = "look";
            return true;
        }

        if (this.activity === "inspect") {
            const target = this.target;
            if (!target) return false;
            if (!this.actor.lookAt(target.pos.x, target.pos.y, target.pos.z)) return false;
            this.visited.add(target.id);
            this.dirty = true;
            this.pending = "look";
            return true;
        }

        if (this.activity === "socialize") {
            const target = this.target;
            if (!target) return false;
            if (!this.actor.face(target.id)) return false;
            this.memory.lastMet = { id: target.id, name: target.name, at: Date.now() };
            this.dirty = true;
            if (!this.actor.wait(2.5 + this.random() * 2)) return false;
            this.pending = "wait";
            return true;
        }

        if (this.activity === "rest") {
            if (!this.actor.wait(this.restSeconds)) return false;
            this.pending = "wait";
            return true;
        }

        if (!this.actor.wait(1.5 + this.random() * 2)) return false;
        this.pending = "wait";
        return true;
    }

    private complete(now: number): void {
        const activity = this.activity;
        this.pending = null;
        this.target = null;
        this.dirty = true;

        if (activity === "rest") this.needs.energy = clamp01(this.needs.energy + 0.35);
        else if (activity === "socialize") this.needs.social = clamp01(this.needs.social - 0.4);
        else if (activity === "inspect") this.needs.curiosity = clamp01(this.needs.curiosity - 0.35);
        else if (activity === "forage") {
            this.needs.curiosity = clamp01(this.needs.curiosity - 0.25);
            this.homesick = true;
        } else if (activity === "return") {
            this.homesick = false;
        } else if (activity === "flee") this.needs.energy = clamp01(this.needs.energy - 0.05);

        const [minimum, maximum] = COOLDOWN_SECONDS[activity];
        this.cooldownUntil[activity] = now + (minimum + this.random() * (maximum - minimum)) * 1000;
    }

    private abandon(now: number): void {
        this.log?.(`${this.activity} failed`);
        this.pending = null;
        if (this.activity === "inspect" && this.target) {
            this.blocked.set(this.target.id, now + 5 * 60 * 1000);
            this.dirty = true;
        }
        const activity = this.activity;
        this.target = null;
        if (activity === "forage") {
            this.cooldownUntil[activity] = now + 20_000 + this.random() * 20_000;
        } else {
            this.cooldownUntil[activity] = now + 3000 + this.random() * 3000;
        }
    }

    private pause(now: number): void {
        this.activity = "wander";
        if (this.actor.wait(1)) this.pending = "wait";
        else this.cooldownUntil.wander = now + 1000;
    }

    private updateNeeds(view: NpcView, elapsed: number): void {
        if (elapsed <= 0) return;
        const moving = view.intent?.kind === "moveTo" && view.intent.status === "running";
        const resting = this.activity === "rest" && this.pending === "wait";
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
        const candidates = view.objects.filter(entry => entry.visible && !entry.isDead && (entry.kind === "bed" || entry.kind === "door"));
        return candidates.find(entry =>
            view.nearby.every(
                other =>
                    !other.isPlayer ||
                    Math.hypot(other.pos.x - entry.pos.x, other.pos.z - entry.pos.z) > 12
            )
        ) ?? null;
    }

    private ready(activity: VillagerActivity, now: number): boolean {
        return this.cooldownUntil[activity] <= now;
    }

    private wanderPoint(view: NpcView, minimum: number, maximum: number): NpcPos {
        return this.pointAround(this.home, minimum, maximum, view.y);
    }

    private pointAround(origin: NpcPos, minimum: number, maximum: number, y: number): NpcPos {
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
