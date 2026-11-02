/**
 * Server-side chat output.
 *
 * Messages are sent as the game's own chat call addressed to every peer. Clients apply their
 * own text/permission settings, so delivery is best effort — do not build authorization on it.
 */
export interface ChatApi {
    send(text: string): void;
    /** Send to one peer (uid from `players.list()`). */
    sendTo(uid: number, text: string): void;
    /** False until the network layer exists (early boot, or a non-network scene). */
    available(): boolean;
}
