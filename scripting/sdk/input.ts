import { context } from "./runtime";
import type { KeyState } from "./api/types";

export type KeyboardPhase = "press" | "hold" | "release";

/** Return true to consume the input for this frame. */
export type KeyboardHandler = (action: string, phase: KeyboardPhase) => boolean | void;

/** Keyboard bindings and phase callbacks; all state is owned by the host. */
export const input = {
    /** Maps a named action to a key name such as "F5" or "LeftShift". */
    bind(action: string, key: string): void {
        context().host.api.input.bind(action, key);
    },

    /** Removes an action's key mapping. */
    unbind(action: string): void {
        context().host.api.input.unbind(action);
    },

    /** Registers a callback for press, hold or release. */
    on(action: string, phase: KeyboardPhase, handler: KeyboardHandler): void {
        context().host.api.input.on(action, phase, (fired, firedPhase) => {
            return handler(String(fired), String(firedPhase) as KeyboardPhase) === true;
        });
    },

    /** Removes a phase callback. */
    off(action: string, phase: KeyboardPhase): void {
        context().host.api.input.off(action, phase);
    },

    /** State of a named action, binding it to a raw key name when needed. */
    state(action: string): KeyState | null {
        return context().host.api.input.state(action);
    },

    /** State of a raw key name without registering an action. */
    rawState(key: string): KeyState | null {
        return context().host.api.input.rawState(key);
    },

    /** True when any bound callback claimed input this frame. */
    handledAny(): boolean {
        return context().host.api.input.handledAny();
    },

    /** Removes every binding and callback owned by this plugin. */
    clear(): void {
        context().host.api.input.clear();
    }
};
