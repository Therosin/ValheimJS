import { ai, importText } from "../../../sdk/index";
import type { VillagerActivity } from "../definitions/types";

/**
 * Loads the shipped YAML behaviour library from the plugin folder. Files are copied next to the
 * bundle by `build.ts`; runtime reads go through the read-only `assets` binding.
 *
 * Behaviour trees need YAML *text* for `ai.compile`, so this uses `importText`, not `importYaml`.
 * Every activity is compiled once in plugin setup; each NPC instantiates the tree it selects.
 */
function read(path: string): string {
    return importText(`behaviours/${path}`);
}

const ACTIVITY_FILES: Record<VillagerActivity, string> = {
    wander: "wander.yaml",
    inspect: "inspect.yaml",
    socialize: "socialize.yaml",
    forage: "forage.yaml",
    explore: "explore.yaml",
    seekHome: "seek-home.yaml",
    return: "return-home.yaml",
    rest: "rest.yaml",
    flee: "flee.yaml",
    eat: "eat.yaml"
};

export function compileBehaviours(): Record<VillagerActivity, string> {
    const imports = {
        move: read("micro/move.yaml"),
        wait: read("micro/wait.yaml"),
        look: read("micro/look.yaml"),
        "look-around": read("micro/look-around.yaml"),
        face: read("micro/face.yaml"),
        claim: read("micro/claim.yaml")
    };

    const trees = {} as Record<VillagerActivity, string>;
    for (const activity of Object.keys(ACTIVITY_FILES) as VillagerActivity[]) {
        trees[activity] = ai.compile(read(ACTIVITY_FILES[activity]), {
            source: `behaviours/${ACTIVITY_FILES[activity]}`,
            imports
        });
    }
    return trees;
}
