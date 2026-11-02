/**
 * Per-plugin document database.
 *
 * Backend: one JSON file per document under `<data>/db/<collection>/<id>.json`, written
 * atomically by the host. Documents live in memory after first read, so `get`/`all` are cheap;
 * a `save` costs one small file write — batch writes rather than saving inside `onServerTick`.
 *
 * ```ts
 * const quests = ctx.db.use<Quest>("quests");
 * quests.save({ id: "intro", title: "Wake up", done: false });
 * quests.update("intro", { done: true });
 * quests.find((q) => !q.done);
 * ```
 *
 * Every document needs an `id` (string or number). Use stable ids: a `steamId`, a prefab name,
 * a quest slug — never an object reference or a timestamp.
 */

/** Anything stored by the database must be JSON-serialisable and carry a stable id. */
export interface Doc {
    id: string | number;
}

/** The scoped host bindings this module sits on (`host.api.db`). */
export interface DatabaseApi {
    get(collection: string, id: string): unknown;
    has(collection: string, id: string): boolean;
    all(collection: string): unknown;
    keys(collection: string): string[];
    count(collection: string): number;
    /** Returns true when the document was created, false when it was replaced. */
    put(collection: string, id: string, json: string): boolean;
    delete(collection: string, id: string): boolean;
    clear(collection: string): number;
    collections(): string[];
}

export type Predicate<T> = (document: T) => boolean;

/** A named table of documents. */
export class Collection<T extends Doc> {
    constructor(private readonly api: DatabaseApi, readonly name: string) {}

    /** The document, or null when it does not exist. */
    get(id: string | number): T | null {
        const document = this.api.get(this.name, String(id));
        return (document as T | null) ?? null;
    }

    has(id: string | number): boolean {
        return this.api.has(this.name, String(id));
    }

    /** Every document; filters like `find` run in the script, not the store. */
    all(): T[] {
        return (this.api.all(this.name) as T[] | null) ?? [];
    }

    /** Ids only — cheaper than `all()` when you just need the keys. */
    keys(): string[] {
        return this.api.keys(this.name) ?? [];
    }

    count(): number {
        return this.api.count(this.name);
    }

    find(predicate: Predicate<T> = () => true): T[] {
        return this.all().filter(predicate);
    }

    first(predicate: Predicate<T> = () => true): T | null {
        for (const document of this.all()) {
            if (predicate(document)) {
                return document;
            }
        }
        return null;
    }

    /** Inserts a new document; throws if the id already exists (use `save` to overwrite). */
    insert(document: T): T {
        if (this.has(document.id)) {
            throw new Error(`${this.name}: document '${String(document.id)}' already exists`);
        }
        return this.save(document);
    }

    /** Insert or replace. Returns the stored document. */
    save(document: T): T {
        requireId(document, this.name);
        this.api.put(this.name, String(document.id), JSON.stringify(document));
        return document;
    }

    /** Shallow-merges `patch` into an existing document. Returns null when it does not exist. */
    update(id: string | number, patch: Partial<T>): T | null {
        const current = this.get(id);
        if (!current) {
            return null;
        }
        const next = { ...current, ...patch, id: current.id } as T;
        return this.save(next);
    }

    /** Read-modify-write in one call; creates the document from `init` when missing. */
    upsert(id: string | number, init: () => T, patch?: (document: T) => Partial<T>): T {
        const current = this.get(id);
        if (!current) {
            const created = init();
            return this.save({ ...created, id } as T);
        }
        const changes = patch ? patch(current) : {};
        return this.save({ ...current, ...changes, id: current.id } as T);
    }

    /** Applies a patch to every matching document. Returns how many were written. */
    updateWhere(predicate: Predicate<T>, patch: (document: T) => Partial<T>): number {
        let written = 0;
        for (const document of this.all()) {
            if (!predicate(document)) {
                continue;
            }
            this.save({ ...document, ...patch(document), id: document.id } as T);
            written += 1;
        }
        return written;
    }

    remove(id: string | number): boolean {
        return this.api.delete(this.name, String(id));
    }

    /** Deletes every document in the collection. Returns how many were removed. */
    clear(): number {
        return this.api.clear(this.name);
    }
}

/** `ctx.db` — one database per plugin, isolated by the host. */
export class Database {
    constructor(private readonly api: DatabaseApi) {}

    /** Open (or create) a named table. `use` is the primary name; `collection` reads the same. */
    use<T extends Doc = Doc>(name: string): Collection<T> {
        requireName(name);
        return new Collection<T>(this.api, name);
    }

    collection<T extends Doc = Doc>(name: string): Collection<T> {
        return this.use<T>(name);
    }

    /** Shorthand for `use(name).get(id)`. */
    get<T extends Doc = Doc>(name: string, id: string | number): T | null {
        return this.use<T>(name).get(id);
    }

    /** Shorthand for `use(name).save(doc)`. */
    set<T extends Doc>(name: string, document: T): T {
        return this.use<T>(name).save(document);
    }

    /** Shorthand for `use(name).all()`. */
    all<T extends Doc = Doc>(name: string): T[] {
        return this.use<T>(name).all();
    }

    count(name: string): number {
        return this.use(name).count();
    }

    /** Tables that already have data on disk. */
    collections(): string[] {
        return this.api.collections() ?? [];
    }
}

function requireName(name: string): void {
    if (typeof name !== "string" || name.trim().length === 0) {
        throw new Error("database collection name must be a non-empty string");
    }
}

function requireId(document: Doc, collection: string): void {
    const id: unknown = document.id;
    if (typeof id !== "string" && typeof id !== "number") {
        throw new Error(`${collection}: documents need a string or number id`);
    }
    if (typeof id === "string" && id.length === 0) {
        throw new Error(`${collection}: document id must not be empty`);
    }
}

/** Builds the `ctx.db` facade over the scoped host bindings. */
export function createDatabase(api: DatabaseApi): Database {
    return new Database(api);
}
