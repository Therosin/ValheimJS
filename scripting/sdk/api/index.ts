import type { ChatApi } from "./chat";
import type { EnvApi } from "./env";
import type { FsApi } from "./fs";
import type { InventoryApi } from "./inventory";
import type { PlayerApi } from "./player";
import type { SpawnApi } from "./spawn";
import type { NetApi } from "./net";
import type { PlayersApi } from "./players";
import type { WorldApi, WorldSnapshot } from "./world";
import type { ZdoApi } from "./zdo";

export type { ChatApi, EnvApi, FsApi, InventoryApi, NetApi, PlayerApi, PlayersApi, SpawnApi, WorldApi, WorldSnapshot, ZdoApi };
export type { ItemStack } from "./inventory";
export type { PlayerStats } from "./player";

/** Everything the C# side installs under `host.api`, one property per binding module. */
import type { DatabaseApi } from "../database";

export interface Api {
    /** Raw scoped bindings; scripts normally use `ctx.db` instead. */
    db: DatabaseApi;
    fs: FsApi;
    world: WorldApi;
    players: PlayersApi;
    zdo: ZdoApi;
    chat: ChatApi;
    env: EnvApi;
    net: NetApi;
    player: PlayerApi;
    inventory: InventoryApi;
    spawn: SpawnApi;
}
