/** Sandboxed file access. Paths are relative to `BepInEx/config/ValheimJS/data`. */
export interface FsApi {
    /** Absolute sandbox root. */
    root(): string;
    exists(path: string): boolean;
    read(path: string): string | null;
    write(path: string, content: string): void;
    append(path: string, content: string): void;
    list(path: string): string[];
    mkdir(path: string): void;
    delete(path: string): void;
    join(left: string, right: string): string;
}
