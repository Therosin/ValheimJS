import { afterEach, describe, expect, test } from "bun:test";
import { canCraft, craft, defineRecipe, registerCondition } from "../sdk/crafting";
import type { CraftCheck, CraftResult, NpcView } from "../sdk/api/types";
import type { Inventory, ValheimBaseNPC } from "../sdk/api/generated";

function install(recipeChecks: Record<string, CraftCheck>, view: NpcView | null, counts: Record<string, number>): string[] {
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
                            recipeChecks[recipe] ?? { ok: true, reasons: [] },
                        craft: (_entity: unknown, recipe: string) => {
                            craftCalls.push(recipe);
                            return { ok: true, reasons: [], count: 1 } as CraftResult;
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

describe("crafting", () => {
    test("merges native reasons and plugin conditions", () => {
        install({ Club: { ok: false, reasons: [{ code: "missing-ingredient", item: "Wood", need: 2, have: 1 }] } }, view({ x: 0, z: 0 }, { x: 20, z: 0 }), {});
        defineRecipe("club-native", { recipe: "Club", conditions: [{ condition: "atHome" }] });

        const check = canCraft(npc(), "club-native");
        expect(check.ok).toBe(false);
        expect(check.reasons.map(reason => reason.code)).toEqual(["missing-ingredient", "not-at-home"]);
    });

    test("built-in conditions read native facts", () => {
        install({ Club: { ok: true, reasons: [] } }, view({ x: 0, z: 0 }, { x: 0, z: 0 }, true), { Wood: 3 });
        defineRecipe("club-swim", { recipe: "Club", conditions: [{ condition: "notSwimming" }] });
        defineRecipe("club-wood", { recipe: "Club", conditions: [{ condition: "hasItem", item: "Wood", count: 2 }] });
        defineRecipe("club-no-wood", { recipe: "Club", conditions: [{ condition: "hasItem", item: "Wood", count: 5 }] });

        expect(canCraft(npc(), "club-swim").reasons.map(reason => reason.code)).toEqual(["swimming"]);
        expect(canCraft(npc(), "club-wood").ok).toBe(true);
        expect(canCraft(npc(), "club-no-wood").reasons[0]?.code).toBe("missing-item");
    });

    test("custom conditions run and failures are reported", () => {
        install({ Club: { ok: true, reasons: [] } }, view({ x: 0, z: 0 }, { x: 0, z: 0 }), {});
        registerCondition("inPool", () => false);
        defineRecipe("club-pool", { recipe: "Club", conditions: [{ condition: "inPool" }] });

        expect(canCraft(npc(), "club-pool").reasons[0]?.code).toBe("inPool");
    });

    test("craft only reaches native after checks pass", () => {
        const calls = install({ Club: { ok: true, reasons: [] } }, view({ x: 0, z: 0 }, { x: 0, z: 0 }), {});
        defineRecipe("club-craft", { recipe: "Club" });

        const result = craft(npc(), "club-craft");
        expect(result.ok).toBe(true);
        expect(calls).toEqual(["Club"]);

        install({ Club: { ok: false, reasons: [{ code: "recipe-missing" }] } }, null, {});
        const blocked = craft(npc(), "club-craft");
        expect(blocked.ok).toBe(false);
        expect(blocked.reason).toBe("recipe-missing");
    });
});

afterEach(() => {
    Reflect.deleteProperty(globalThis, "__vjs_context");
});
