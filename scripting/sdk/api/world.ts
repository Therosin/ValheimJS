export interface WorldSnapshot {
    world: string | null;
    peers: number;
    day: number;
    dayFraction: number;
    timeOfDay: "day" | "night" | "unknown";
}

/** Live server world state. Values are read per call; at boot they read as null/zero. */
export interface WorldApi {
    snapshot(): WorldSnapshot;
    name(): string | null;
    peers(): number;
    day(): number;
    dayFraction(): number;
    timeOfDay(): "day" | "night" | "unknown";
}
