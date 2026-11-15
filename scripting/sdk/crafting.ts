import { context } from "./runtime";
import type { Inventory, ValheimBaseNPC } from "./api/generated";
import type { CraftCheck, CraftReason, CraftResult, NpcView } from "./api/types";

/**
 * Standard equipment crafting: native ObjectDB recipes plus plugin-defined conditions.
 *
 * The native executor validates the recipe, station/level and ingredients, then consumes and
 * produces the output (into the inventory or on the ground). Plugin conditions are evaluated
 * here, before the native transaction, and only describe whether this recipe is valid for this
 * NPC. They never decide whether the NPC wants to craft.
 */

export interface ConditionRef {
    condition: string;
    item?: string;
    count?: number;
    radius?: number;
    [key: string]: unknown;
}

export interface RecipeDefinition {
    /** Native recipe name; defaults to the definition id. */
    recipe?: string;
    conditions?: ConditionRef[];
}

export interface CraftConditionContext {
    readonly actor: ValheimBaseNPC;
    readonly view: NpcView | null;
    readonly recipe: string;
    readonly inventory: Inventory;
    readonly now: number;
}

export type CraftConditionResult =
    | boolean
    | { ok: boolean; reasons?: CraftReason[] };

export type CraftCondition = (
    context: CraftConditionContext,
    ref: ConditionRef
) => CraftConditionResult;

const recipes = new Map<string, RecipeDefinition>();
const conditions = new Map<string, CraftCondition>();

export function defineRecipe(id: string, definition: RecipeDefinition = {}): void {
    recipes.set(id, definition);
}

export function registerCondition(name: string, handler: CraftCondition): void {
    conditions.set(name, handler);
}

export function canCraft(entity: ValheimBaseNPC, recipe: string): CraftCheck {
    const definition = recipes.get(recipe);
    const nativeName = definition?.recipe ?? recipe;
    const api = context().host.api.npc;
    const reasons: CraftReason[] = [];

    const native = api.craftCheck(entity, nativeName) as CraftCheck | undefined;
    if (native === undefined) {
        reasons.push({ code: "native-unavailable" });
    } else {
        reasons.push(...(native.reasons ?? []));
    }

    if (definition?.conditions !== undefined) {
        const view = api.perception(entity) as NpcView | null;
        const inventory = api.inventory(entity) as Inventory;
        for (const ref of definition.conditions) {
            const handler = conditions.get(ref.condition);
            if (handler === undefined) {
                reasons.push({ code: "condition-missing", condition: ref.condition });
                continue;
            }

            let result: CraftConditionResult;
            try {
                result = handler({ actor: entity, view, recipe, inventory, now: Date.now() }, ref);
            } catch {
                reasons.push({ code: "condition-failed", condition: ref.condition });
                continue;
            }

            if (result === false) {
                reasons.push({ code: ref.condition });
            } else if (typeof result === "object" && !result.ok) {
                reasons.push(...(result.reasons ?? [{ code: ref.condition }]));
            }
        }
    }

    return { ok: reasons.length === 0, reasons };
}

export function craft(entity: ValheimBaseNPC, recipe: string): CraftResult {
    const definition = recipes.get(recipe);
    const nativeName = definition?.recipe ?? recipe;
    const check = canCraft(entity, recipe);
    if (!check.ok) {
        return {
            ok: false,
            reason: check.reasons[0]?.code ?? "invalid",
            reasons: check.reasons,
            count: 0
        };
    }

    const result = context().host.api.npc.craft(entity, nativeName) as CraftResult | undefined;
    return result ?? { ok: false, reason: "native-unavailable", reasons: [], count: 0 };
}

registerCondition("notSwimming", ({ view }) =>
    view?.swimming === true
        ? { ok: false, reasons: [{ code: "swimming" }] }
        : true);

registerCondition("atHome", ({ view }, ref) => {
    if (view === null) {
        return { ok: false, reasons: [{ code: "no-body" }] };
    }

    const radius = typeof ref.radius === "number" ? ref.radius : 5;
    const dx = view.x - view.home.x;
    const dz = view.z - view.home.z;
    return Math.hypot(dx, dz) <= radius
        ? true
        : { ok: false, reasons: [{ code: "not-at-home" }] };
});

registerCondition("hasItem", ({ inventory }, ref) => {
    const item = typeof ref.item === "string" ? ref.item : "";
    const need = typeof ref.count === "number" ? ref.count : 1;
    const have = inventory.count(item);
    return have >= need
        ? true
        : { ok: false, reasons: [{ code: "missing-item", item, need, have }] };
});

registerCondition("missingItem", ({ inventory }, ref) => {
    const item = typeof ref.item === "string" ? ref.item : "";
    const have = inventory.count(item);
    return have === 0
        ? true
        : { ok: false, reasons: [{ code: "already-owned", item, have }] };
});
