import { context } from "./runtime";
import type { DbApi } from "./api/generated";

/** Anything stored by the database must be JSON-serialisable and carry a stable id. */
export interface Doc {
    id: string | number;
}

export type Predicate<T> = (document: T) => boolean;

/**
 * A named collection of documents. One JSON file per document under
 * `<data>/db/<collection>/`; the host owns durability, this class owns the ergonomics.
 */
export class Collection<T extends Doc> {
    constructor(private readonly api: DbApi, readonly name: string) {}

    get(id: string | number): T | null {
        return (this.api.get(this.name, String(id)) as T | null) ?? null;
    }

    has(id: string | number): boolean {
        return this.api.has(this.name, String(id));
    }

    /** Every document; predicates run in this script, the loop in the host. */
    all(): T[] {
        return (this.api.all(this.name) as T[] | null) ?? [];
    }

    keys(): string[] {
        return this.api.keys(this.name) ?? [];
    }

    count(): number {
        return this.api.count(this.name);
    }

    /** The host parses each document and calls the predicate; matching documents come back. */
    find(predicate: Predicate<T> = () => true): T[] {
        return this.api.find(this.name, (document) => predicate(document as T)) as T[];
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

    /** Insert or replace. */
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

        return this.save({ ...current, ...patch, id: current.id } as T);
    }

    /** Read-modify-write in one call; creates the document from `init` when missing. */
    upsert(id: string | number, init: () => T, patch?: (document: T) => Partial<T>): T {
        const current = this.get(id);
        if (!current) {
            return this.save({ ...init(), id } as T);
        }

        return this.save({ ...current, ...(patch ? patch(current) : {}), id: current.id } as T);
    }

    /** Applies a patch to every matching document. Returns how many were written. */
    updateWhere(predicate: Predicate<T>, patch: (document: T) => Partial<T>): number {
        return this.api.updateWhere(
            this.name,
            (document) => predicate(document as T),
            (document) => {
                const current = document as T;
                return { ...current, ...patch(current), id: current.id };
            }
        );
    }

    remove(id: string | number): boolean {
        return this.api.delete(this.name, String(id));
    }

    /** Deletes every document. Returns how many were removed. */
    clear(): number {
        return this.api.clear(this.name);
    }
}

/** `ctx.db` — one database per plugin, isolated by the host. */
export class Db {
    constructor(private readonly api: DbApi) {}

    /** Open (or create) a named collection. */
    use<T extends Doc = Doc>(name: string): Collection<T> {
        requireName(name);
        return new Collection<T>(this.api, name);
    }

    get<T extends Doc = Doc>(name: string, id: string | number): T | null {
        return this.use<T>(name).get(id);
    }

    set<T extends Doc>(name: string, document: T): T {
        return this.use<T>(name).save(document);
    }

    all<T extends Doc = Doc>(name: string): T[] {
        return this.use<T>(name).all();
    }

    count(name: string): number {
        return this.use(name).count();
    }

    /** Collections that already have data on disk. */
    collections(): string[] {
        return this.api.collections() ?? [];
    }
}

/** Resolves the calling plugin's database. */
export function database(): Db {
    return new Db(context().host.api.db);
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
