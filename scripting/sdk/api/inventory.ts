export interface ItemStack {
    /** Prefab name, e.g. "Wood" or "Coins". */
    name: string;
    amount: number;
    quality: number;
    slot: { x: number; y: number };
}

/** Player inventory. Item names are prefab names; unknown names are rejected, not guessed. */
export interface InventoryApi {
    list(uid: number): ItemStack[] | null;
    count(uid: number, item: string): number;
    add(uid: number, item: string, amount: number): void;
    remove(uid: number, item: string, amount: number): void;
    has(uid: number, item: string, amount: number): boolean;
    /** Every item prefab the game knows — useful for validation and tooling. */
    items(): string[];
}
