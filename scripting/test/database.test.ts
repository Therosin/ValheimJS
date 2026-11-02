import { describe, expect, test } from "bun:test";
import { createDatabase, type DatabaseApi } from "../sdk/database";

/** In-memory stand-in for the scoped `host.api.db` bindings. */
function fakeApi(): DatabaseApi {
    const tables = new Map<string, Map<string, string>>();
    const table = (name: string): Map<string, string> => {
        let existing = tables.get(name);
        if (!existing) {
            existing = new Map();
            tables.set(name, existing);
        }
        return existing;
    };
    const parse = (json: string): unknown => JSON.parse(json);
    return {
        get: (collection, id) => {
            const json = table(collection).get(id);
            return json === undefined ? null : parse(json);
        },
        has: (collection, id) => table(collection).has(id),
        all: (collection) => Array.from(table(collection).values()).map(parse),
        keys: (collection) => Array.from(table(collection).keys()),
        count: (collection) => table(collection).size,
        put: (collection, id, json) => {
            const existed = table(collection).has(id);
            table(collection).set(id, json);
            return !existed;
        },
        delete: (collection, id) => table(collection).delete(id),
        clear: (collection) => {
            const removed = table(collection).size;
            table(collection).clear();
            return removed;
        },
        collections: () => Array.from(tables.keys())
    };
}

interface Quest {
    id: string;
    title: string;
    done: boolean;
    reward?: number;
}

describe("database", () => {
    test("stores, reads and replaces documents", () => {
        const db = createDatabase(fakeApi());
        const quests = db.use<Quest>("quests");

        quests.save({ id: "intro", title: "Wake up", done: false });
        expect(quests.get("intro")).toEqual({ id: "intro", title: "Wake up", done: false });
        expect(quests.count()).toBe(1);

        quests.save({ id: "intro", title: "Wake up", done: true });
        expect(quests.get("intro")?.done).toBe(true);
        expect(quests.count()).toBe(1);
    });

    test("insert refuses to overwrite, save does not", () => {
        const quests = createDatabase(fakeApi()).use<Quest>("quests");
        quests.insert({ id: "a", title: "A", done: false });
        expect(() => quests.insert({ id: "a", title: "B", done: false })).toThrow(/already exists/);
        expect(quests.save({ id: "a", title: "B", done: true }).title).toBe("B");
    });

    test("update merges a patch and keeps the id", () => {
        const quests = createDatabase(fakeApi()).use<Quest>("quests");
        quests.save({ id: "a", title: "A", done: false });

        expect(quests.update("a", { done: true })).toEqual({ id: "a", title: "A", done: true });
        expect(quests.update("missing", { done: true })).toBeNull();
    });

    test("update cannot change the id", () => {
        const quests = createDatabase(fakeApi()).use<Quest>("quests");
        quests.save({ id: "a", title: "A", done: false });
        quests.update("a", { id: "b" } as Partial<Quest>);
        expect(quests.get("a")).not.toBeNull();
        expect(quests.get("b")).toBeNull();
    });

    test("upsert creates once and patches afterwards", () => {
        const quests = createDatabase(fakeApi()).use<Quest & { visits: number }>("visits");

        const first = quests.upsert(
            "7656",
            () => ({ id: "7656", title: "", done: false, visits: 0 }),
            (doc) => ({ visits: doc.visits + 1 })
        );
        expect(first.visits).toBe(0);

        const second = quests.upsert(
            "7656",
            () => ({ id: "7656", title: "", done: false, visits: 0 }),
            (doc) => ({ visits: doc.visits + 1 })
        );
        expect(second.visits).toBe(1);
        expect(quests.count()).toBe(1);
    });

    test("find and updateWhere filter in the script", () => {
        const quests = createDatabase(fakeApi()).use<Quest>("quests");
        quests.save({ id: "a", title: "A", done: true });
        quests.save({ id: "b", title: "B", done: false });

        expect(quests.find((quest) => !quest.done).map((quest) => quest.id)).toEqual(["b"]);
        expect(quests.first((quest) => quest.done)?.id).toBe("a");
        expect(quests.updateWhere((quest) => !quest.done, () => ({ done: true }))).toBe(1);
        expect(quests.find((quest) => !quest.done)).toHaveLength(0);
    });

    test("remove and clear report what they changed", () => {
        const quests = createDatabase(fakeApi()).use<Quest>("quests");
        quests.save({ id: "a", title: "A", done: false });
        quests.save({ id: "b", title: "B", done: false });

        expect(quests.remove("a")).toBe(true);
        expect(quests.remove("a")).toBe(false);
        expect(quests.clear()).toBe(1);
        expect(quests.count()).toBe(0);
    });

    test("numeric ids are stored as strings but round-trip through get", () => {
        const numbers = createDatabase(fakeApi()).use<{ id: number; value: string }>("numbers");
        numbers.save({ id: 7, value: "seven" });
        expect(numbers.get(7)?.value).toBe("seven");
        expect(numbers.keys()).toEqual(["7"]);
    });

    test("documents without an id are rejected", () => {
        const db = createDatabase(fakeApi()).use<Quest>("quests");
        expect(() => db.save({ title: "no id", done: false } as Quest)).toThrow(/id/);
        expect(() => createDatabase(fakeApi()).use("  ")).toThrow(/non-empty/);
    });
});
