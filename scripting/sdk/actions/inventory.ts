import { registerAction, type ActionResultLike } from "./registry";

interface EquipConfig {
    equip: unknown;
}

registerAction<EquipConfig>("equip", {
    tick(context, config) {
        return run(context.inventory?.equip(config.equip), "unsupported");
    }
});

registerAction<{ unequip: unknown }>("unequip", {
    tick(context, config) {
        return run(context.inventory?.unequip(config.unequip), "unsupported");
    }
});

registerAction<{ consume: unknown }>("consume", {
    tick(context, config) {
        return run(context.inventory?.use(config.consume), "unsupported");
    }
});

interface DropConfig {
    drop: unknown;
    count?: number;
}

registerAction<DropConfig>("drop", {
    tick(context, config) {
        return run(context.inventory?.drop(config.drop, config.count), "unsupported");
    }
});

function run(result: ActionResultLike | undefined, missing: string) {
    if (result === undefined) {
        return { status: "failure" as const, reason: missing };
    }

    return result.ok
        ? "success" as const
        : { status: "failure" as const, reason: result.reason ?? "rejected" };
}
