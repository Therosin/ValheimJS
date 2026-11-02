/**
 * Which side of the game this process is.
 *
 * `dedicated` is fixed at startup (headless process). `isServer` also becomes true once a
 * player hosts a world, so use it — not `dedicated` — for authority checks.
 */
export interface EnvApi {
    /** Headless dedicated server. */
    dedicated(): boolean;
    /** This process holds authority (dedicated server, or a player hosting). */
    isServer(): boolean;
    /** A process with a local player/UI (true on a hosting player too). */
    isClient(): boolean;
    /** "server" for a dedicated process, otherwise "client". */
    side(): "server" | "client";
    singlePlayer(): boolean;
    peers(): number;
    /** Per-process id; broadcasts loop back, so this identifies the sender's own messages. */
    instance(): string;
}
