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

/** Read a shipped YAML document as text; pass it to `ai.compile` or your own parser. */
export function importYaml(path: string): string {
    return importText(path);
}

/**
 * Read a shipped TOML document as text. ValheimJS does not bundle a TOML parser, so parse it in
 * the plugin (or with a bundled library).
 */
export function importToml(path: string): string {
    return importText(path);
}
