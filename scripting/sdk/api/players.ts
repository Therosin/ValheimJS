export interface PlayerInfo {
    uid: number;
    name: string;
    server: boolean;
    /** Empty until the player has spawned a character. */
    characterId: string;
    /** True when the peer is on the server's admin list. */
    admin: boolean;
    /** SteamID64 digits, or null for non-Steam platforms. Stable across sessions. */
    steamId: string | null;
    /** Raw platform id from the peer socket ("Steam_7656…"). */
    platformId: string | null;
    position: { x: number; y: number; z: number };
}

/** Connected peers as the server sees them. */
export interface PlayersApi {
    list(): PlayerInfo[];
    count(): number;
    names(): string[];
    /** Display name for a peer uid, or null when that peer is gone. */
    hostname(uid: number): string | null;
    /** Server admin list membership. */
    isAdmin(uid: number): boolean;
    /** SteamID64 digits for a peer, or null (non-Steam platform / not connected). */
    steamId(uid: number): string | null;
    /** Raw platform id ("Steam_7656…"). */
    platformId(uid: number): string | null;
}
