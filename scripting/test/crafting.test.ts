import { afterEach, describe, expect, test } from "bun:test";
import { canCraft, craft, defineRecipe, registerCondition } from "../sdk/crafting";
import type { CraftBlock, CraftCheck, CraftMissing, CraftResult, NpcView } from "../sdk/api/types";
import type { Inventory, ValheimBaseNPC } from "../sdk/api/generated";

function check(ok: boolean, blockedBy: CraftBlock[] = [], missing: Partial<CraftMissing> = {}): CraftCheck {
    return {
        ok,
        blockedBy,
        missing: { ingredients: [], station: null, ...missing }
    };
}

function install(
    recipeChecks: Record<string, CraftCheck>,
    view: NpcView | null,
    counts: Record<string, number>
): string[] {
    const craftCalls: string[] = [];
    const inventory = {
        count: (name: string) => counts[name] ?? 0,
        has: (name: string, amount = 1) => (counts[name] ?? 0) >= amount
    };
    Object.defineProperty(globalThis, "__vjs_context", {
        configurable: true,
        value: {
            host: {
                api: {
                    npc: {
                        craftCheck: (_entity: unknown, recipe: string) =>
                            recipeChecks[recipe] ?? check(true),
                        craft: (_entity: unknown, recipe: string) => {
                            craftCalls.push(recipe);
                            return {
                                ok: true,
                                missing: { ingredients: [], station: null },
                                blockedBy: [],
                                count: 1
                            } as CraftResult;
                        },
                        perception: () => view,
                        inventory: () => inventory as unknown as Inventory
                    }
                }
            }
        }
    });

    return craftCalls;
}

function npc(): ValheimBaseNPC {
    return {} as ValheimBaseNPC;
}

function view(home: { x: number; z: number }, at: { x: number; z: number }, swimming = false): NpcView {
    return {
        id: "1:1",
        kind: "villager",
        x: at.x,
        y: 0,
        z: at.z,
        health: 100,
        maxHealth: 100,
        isAlive: true,
        faction: "",
        isTamed: false,
        level: 1,
        home: { x: home.x, y: 0, z: home.z },
        ready: true,
        swimming,
        combat: { engaged: false, threatId: null },
        self: null,
        action: null,
        environment: null,
        nearby: [],
        remembered: [],
        objects: []
    };
}

afterEach(() => {
    Reflect.deleteProperty(globalThis, "__vjs_context");
});

describe("crafting", () => {
    test("merges native blockedBy and missing with plugin conditions", () => {
        install(
            {
                Club: check(
                    false,
                    [{ code: "missing-ingredient", item: "Wood", need: 2, have: 1 }],
                    { ingredients: [{ item: "Wood", amount: 1 }] }
                )
            },
            view({ x: 0, z: 0 }, { x: 20, z: 0 }),
            {}
        );
        defineRecipe("club-native", { recipe: "Club", conditions: [{ condition: "atHome" }] });

        const result = canCraft(npc(), "club-native");
        expect(result.ok).toBe(false);
        expect(result.blockedBy.map(block => block.code)).toEqual(["missing-ingredient", "not-at-home"]);
        expect(result.missing.ingredients).toEqual([{ item: "Wood", amount: 1 }]);
    });

    test("built-in conditions read native facts and contribute missing items", () => {
        install({ Club: check(true) }, view({ x: 0, z: 0 }, { x: 0, z: 0 }, true), { Wood: 3 });
        defineRecipe("club-swim", { recipe: "Club", conditions: [{ condition: "notSwimming" }] });
        defineRecipe("club-wood", { recipe: "Club", conditions: [{ condition: "hasItem", item: "Wood", count: 2 }] });
        defineRecipe("club-no-wood", { recipe: "Club", conditions: [{ condition: "hasItem", item: "Wood", count: 5 }] });

        expect(canCraft(npc(), "club-swim").blockedBy.map(block => block.code)).toEqual(["swimming"]);
        expect(canCraft(npc(), "club-wood").ok).toBe(true);
        const missing = canCraft(npc(), "club-no-wood");
        expect(missing.blockedBy[0]?.code).toBe("missing-item");
        expect(missing.missing.ingredients).toEqual([{ item: "Wood", amount: 2 }]);
    });

    test("custom condition failures are reported", () => {
        install({ Club: check(true) }, view({ x: 0, z: 0 }, { x: 0, z: 0 }), {});
        registerCondition("inPool", () => false);
        defineRecipe("club-pool", { recipe: "Club", conditions: [{ condition: "inPool" }] });

        expect(canCraft(npc(), "club-pool").blockedBy[0]?.code).toBe("inPool");
    });

    test("craft only reaches native after checks pass", () => {
        const calls = install({ Club: check(true) }, view({ x: 0, z: 0 }, { x: 0, z: 0 }), {});
        defineRecipe("club-craft", { recipe: "Club" });

        const result = craft(npc(), "club-craft");
        expect(result.ok).toBe(true);
        expect(result.count).toBe(1);
        expect(calls).toEqual(["Club"]);

        install({ Club: check(false, [{ code: "recipe-missing" }]) }, null, {});
        const blocked = craft(npc(), "club-craft");
        expect(blocked.ok).toBe(false);
        expect(blocked.blockedBy[0]?.code).toBe("recipe-missing");
    });
});
