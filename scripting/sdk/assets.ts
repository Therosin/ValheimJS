import { context } from "./runtime";

/**
 * Read-only files shipped inside the plugin folder. The data sandbox (`fs`) is separate: assets
 * are for YAML/TOML/config templates bundled with the mod, not mutable state.
 */
export const assets = {
    /** Absolute path of this plugin's folder. */
    root(): string {
        return context().host.api.assets.root();
    },
    exists(path: string): boolean {
        return context().host.api.assets.exists(path);
    },
    read(path: string): string | null {
        return context().host.api.assets.read(path);
    },
    /** Parsed YAML document (plain objects, arrays, strings, numbers, booleans and null). */
    yaml(path: string): unknown {
        return context().host.api.assets.yaml(path);
    },
    /** Parsed TOML document (plain objects, arrays, strings, numbers, booleans and null). */
    toml(path: string): unknown {
        return context().host.api.assets.toml(path);
    },
    list(path: string): string[] {
        return context().host.api.assets.list(path);
    }
};

/** Read a shipped text file; throws when the file is missing. */
export function importText(path: string): string {
    const text = assets.read(path);
    if (text === null) throw new Error(`asset not found: ${path}`);
    return text;
}

/** Read and parse a shipped YAML document. */
export function importYaml<T = unknown>(path: string): T {
    return assets.yaml(path) as T;
}

/** Read and parse a shipped TOML document. */
export function importToml<T = unknown>(path: string): T {
    return assets.toml(path) as T;
}
