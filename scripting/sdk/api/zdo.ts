/** World object table counters (ZDOMan). */
export interface ZdoApi {
    count(): number;
    /** False when the game build hides the object table; treat counts as unavailable. */
    available(): boolean;
}
