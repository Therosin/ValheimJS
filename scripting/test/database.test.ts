import { describe, expect, test } from "bun:test";
import { Db } from "../sdk/database";
import type { DbApi } from "../sdk/api/generated";

function memoryDb(): DbApi & { store: Map<string, Map<string, unknown>> } {
    const store = new Map<string, Map<string, unknown>>();
    const collection = (name: string) => {
        let table = store.get(name);
        if (!table) store.set(name, (table = new Map()));
        return table;
    };
    const documents = (name: string) => [...collection(name).values()];

    return {
        store,
        get: (name, id) => collection(name).get(id) ?? null,
        has: (name, id) => collection(name).has(id),
        all: (name) => documents(name),
        keys: (name) => [...collection(name).keys()],
        count: (name) => collection(name).size,
        put: (name, id, json) => {
            const existed = collection(name).has(id);
            collection(name).set(id, JSON.parse(json));
            return !existed;
        },
        delete: (name, id) => collection(name).delete(id),
        clear: (name) => {
            const size = collection(name).size;
            collection(name).clear();
            return size;
        },
        collections: () => [...store.keys()],
        find: (name, predicate) => documents(name).filter((doc) => predicate(doc)),
        updateWhere: (name, predicate, patch) => {
            let written = 0;
            for (const [id, doc] of collection(name)) {
                if (!predicate(doc)) continue;
                collection(name).set(id, patch(doc));
                written++;
            }
            return written;
        }
    };
}

interface Quest {
    id: string;
    title: string;
    done: boolean;
}

describe("database facade", () => {
    test("saves, reads and updates documents", () => {
        const db = new Db(memoryDb());
        const quests = db.use<Quest>("quests");

        expect(quests.save({ id: "intro", title: "Wake up", done: false })).toEqual({
            id: "intro",
            title: "Wake up",
            done: false
        });
        expect(quests.get("intro")?.title).toBe("Wake up");
        expect(quests.has("intro")).toBe(true);
        expect(quests.update("intro", { done: true })?.done).toBe(true);
        expect(quests.count()).toBe(1);
        expect(quests.keys()).toEqual(["intro"]);
    });

    test("insert rejects an existing id", () => {
        const quests = new Db(memoryDb()).use<Quest>("quests");
        quests.save({ id: "intro", title: "Wake up", done: false });
        expect(() => quests.insert({ id: "intro", title: "Again", done: false })).toThrow("already exists");
    });

    test("predicates and patches run through the host bindings", () => {
        const db = new Db(memoryDb());
        const quests = db.use<Quest>("quests");
        quests.save({ id: "a", title: "A", done: false });
        quests.save({ id: "b", title: "B", done: true });

        expect(quests.find((quest) => !quest.done).map((quest) => quest.id)).toEqual(["a"]);
        expect(quests.first((quest) => quest.done)?.id).toBe("b");
        expect(quests.updateWhere((quest) => !quest.done, () => ({ done: true }))).toBe(1);
        expect(quests.all().every((quest) => quest.done)).toBe(true);
    });

    test("requires a stable id", () => {
        const quests = new Db(memoryDb()).use<Quest>("quests");
        expect(() => quests.save({ title: "no id", done: false } as unknown as Quest)).toThrow("need a string or number id");
    });
});
