/**
 * Script-to-script messaging between the server and connected clients.
 *
 * Payloads are JSON strings; the channel name is namespaced on the wire. Both sides must call
 * `listen` for a channel before anything can arrive on it.
 */
export interface NetApi {
    send(channel: string, payloadJson: string): void;
    sendTo(uid: number, channel: string, payloadJson: string): void;
    /** Idempotent; registers the channel with the game's routing layer. */
    listen(channel: string): void;
    /** Channels this process is listening on. */
    channels(): string[];
    /** False until networking exists (early boot, single-player scene). */
    available(): boolean;
    side(): "server" | "client";
    /** Peer id of the server (0 on the server process itself). */
    serverPeer(): number;
    listenFrame(channel: string): void;
    sendFrame(channel: string, frameBase64: string): void;
    sendFrameTo(uid: number, channel: string, frameBase64: string): void;
}
