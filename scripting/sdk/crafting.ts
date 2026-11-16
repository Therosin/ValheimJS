import { context } from "./runtime";
import type { Inventory, ValheimBaseNPC } from "./api/generated";
import type { CraftBlock, CraftCheck, CraftMissing, CraftResult, NpcView } from "./api/types";

/**
 * Standard equipment crafting: native ObjectDB recipes plus plugin-defined conditions.
 *
 * `missing` is the sourceable shortfall; `blockedBy` is why the recipe is currently blocked.
 * Conditions may contribute to both. They never decide whether the NPC wants to craft.
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
    | {
        ok: boolean;
        blockedBy?: CraftBlock[];
        missing?: Partial<CraftMissing>;
    };

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

    const native = api.craftCheck(entity, nativeName) as CraftCheck | undefined;
    const blockedBy: CraftBlock[] = [...(native?.blockedBy ?? [])];
    const missing: CraftMissing = {
        ingredients: [...(native?.missing?.ingredients ?? [])],
        station: native?.missing?.station ?? null
    };

    if (native === undefined) {
        blockedBy.push({ code: "native-unavailable" });
    }

    for (const ref of definition?.conditions ?? []) {
        const handler = conditions.get(ref.condition);
        if (handler === undefined) {
            blockedBy.push({ code: "condition-missing" });
            continue;
        }

        let result: CraftConditionResult;
        try {
            result = handler(
                {
                    actor: entity,
                    view: api.perception(entity) as NpcView | null,
                    recipe,
                    inventory: api.inventory(entity) as Inventory,
                    now: Date.now()
                },
                ref
            );
        } catch {
            blockedBy.push({ code: "condition-failed" });
            continue;
        }

        if (result === false) {
            blockedBy.push({ code: ref.condition });
        } else if (typeof result === "object" && !result.ok) {
            blockedBy.push(...(result.blockedBy ?? [{ code: ref.condition }]));
            if (result.missing?.ingredients !== undefined) {
                missing.ingredients.push(...result.missing.ingredients);
            }

            if (result.missing?.station !== undefined && missing.station === null) {
                missing.station = result.missing.station;
            }
        }
    }

    return { ok: blockedBy.length === 0, missing, blockedBy };
}

export function craft(entity: ValheimBaseNPC, recipe: string): CraftResult {
    const definition = recipes.get(recipe);
    const nativeName = definition?.recipe ?? recipe;
    const check = canCraft(entity, recipe);
    if (!check.ok) {
        return { ...check, count: 0 };
    }

    const result = context().host.api.npc.craft(entity, nativeName) as CraftResult | undefined;
    return result ?? {
        ok: false,
        missing: check.missing,
        blockedBy: [{ code: "native-unavailable" }],
        count: 0
    };
}

registerCondition("notSwimming", ({ view }) =>
    view?.swimming === true
        ? { ok: false, blockedBy: [{ code: "swimming" }] }
        : true);

registerCondition("atHome", ({ view }, ref) => {
    if (view === null) {
        return { ok: false, blockedBy: [{ code: "no-body" }] };
    }

    const radius = typeof ref.radius === "number" ? ref.radius : 5;
    const dx = view.x - view.home.x;
    const dz = view.z - view.home.z;
    return Math.hypot(dx, dz) <= radius
        ? true
        : { ok: false, blockedBy: [{ code: "not-at-home" }] };
});

registerCondition("hasItem", ({ inventory }, ref) => {
    const item = typeof ref.item === "string" ? ref.item : "";
    const need = typeof ref.count === "number" ? ref.count : 1;
    const have = inventory.count(item);
    if (have >= need) {
        return true;
    }

    return {
        ok: false,
        blockedBy: [{ code: "missing-item", item, need, have }],
        missing: { ingredients: [{ item, amount: need - have }] }
    };
});

registerCondition("missingItem", ({ inventory }, ref) => {
    const item = typeof ref.item === "string" ? ref.item : "";
    const have = inventory.count(item);
    return have === 0
        ? true
        : { ok: false, blockedBy: [{ code: "already-owned", item, have }] };
});
