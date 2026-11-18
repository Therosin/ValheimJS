import { describe, expect, test } from "bun:test";
import type { InventoryItem } from "../sdk/api/types";
import type { VillagerContext } from "../testing/life-demo/definitions/types";
import { cookableFood, storableItems } from "../testing/life-demo/definitions/activities";

const item = (prefab: string, type: InventoryItem["type"], stack = 1): InventoryItem =>
    ({ prefab, type, stack } as InventoryItem);

const context = (items: InventoryItem[]): VillagerContext =>
    ({ actor: { getInventory: () => ({ items }) } } as unknown as VillagerContext);

describe("storage supply", () => {
    test("deposits only configured materials", () => {
        const entries = storableItems(
            context([
                item("Wood", "material", 3),
                item("Stone", "material", 2),
                item("Club", "weapon"),
                item("Raspberry", "food")
            ])
        );
        expect(entries.map(entry => entry.prefab)).toEqual(["Wood", "Stone"]);
    });

    test("cooking supply counts raw food but not cooked food", () => {
        expect(
            cookableFood(
                context([
                    item("RawMeat", "food"),
                    item("CookedMeat", "food"),
                    item("Raspberry", "food")
                ])
            )
        ).toBe(2);
    });
});
